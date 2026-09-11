namespace Embedding_Console.Processors;

using Embedding_Console.Models;
using Embedding_Console.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Orchestrates the JSON ETL pipeline: extracts chunks, generates embeddings, 
/// builds Qdrant points, and attaches them to the original payload.
/// </summary>
public class EmbeddingProcessor
{
    private readonly IEmbeddingService _embeddingService;
    private readonly int? _expectedDimension;
    private readonly string? _vectorName;
    private int? _detectedDim;

    /// <summary>
    /// Result of processing a single chunk.
    /// </summary>
    public record ChunkResult(
        string ChunkId,
        string ContentKey,
        bool Success,
        string? Error,
        QdrantPoint? Point = null);

    public EmbeddingProcessor(
        IEmbeddingService embeddingService,
        int? expectedDimension = null,
        string? vectorName = null)
    {
        _embeddingService = embeddingService;
        _expectedDimension = expectedDimension;
        _vectorName = vectorName;
    }

    /// <summary>
    /// Process the entire document: generate embeddings for all chunks and attach Qdrant points.
    /// </summary>
    public async Task<ProcessResult> ProcessAsync(
        JsonNode rootNode,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Find the chunks collection within the JSON structure
        var chunks = ExtractChunks(rootNode);
        if (chunks.Count == 0)
            throw new InvalidOperationException(
                "No chunks found in input JSON. Expected a 'chunks' array at the root level.");

        PrintProgress(inputPath, chunks.Count, "Input", ConsoleColor.White);

        // Create placeholder points arrays on each chunk node
        var chunkResults = new List<ChunkResult>();
        foreach (var chunkInfo in chunks)
        {
            chunkInfo.ChunkNode["points"] = new JsonArray();
        }

        Console.WriteLine();
        PrintProgress(inputPath, chunks.Count, "Generating embeddings", ConsoleColor.Yellow);

        // Process each chunk sequentially (controlled pace for llama.cpp)
        var failedChunks = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkInfo = chunks[i];
            try
            {
                var text = ExtractText(chunkInfo);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.Error.WriteLine($"Warning: Chunk '{chunkInfo.ChunkId}' has empty or null content — skipping.");
                    chunkResults.Add(new ChunkResult(
                        chunkInfo.ChunkId, "document", false, "Empty content"));
                    failedChunks.Add(chunkInfo.ChunkId);
                    continue;
                }

                var vector = await _embeddingService.GenerateEmbeddingAsync(
                    text, cancellationToken);

                // Validate dimension after first successful call
                if (i == 0 && _expectedDimension > 0)
                {
                    if (vector.Length != _expectedDimension.Value)
                        throw new InvalidOperationException(
                            $"Embedding dimension mismatch. Expected: {_expectedDimension.Value}, Actual: {vector.Length}. " +
                            $"First chunk: {chunkInfo.ChunkId}");
                }

                // Track detected dimension for reporting
                if (!_detectedDim.HasValue)
                    _detectedDim = vector.Length;

                var point = BuildQdrantPoint(chunkInfo, vector);

                // Attach the point to the original chunk node as a JSON array
                var pointArray = chunkInfo.ChunkNode["points"] as JsonArray;
                pointArray!.Clear();
                pointArray.Add(BuildPointJson(point, _vectorName));

                chunkResults.Add(new ChunkResult(
                    chunkInfo.ChunkId, "document", true, null, point));
            }
            catch (Exception ex) when (!(ex is InvalidOperationException && ex.Message.StartsWith("No chunks found")))
            {
                Console.Error.WriteLine($"Error: Failed to generate embedding for chunk '{chunkInfo.ChunkId}': {ex.Message}");

                var errorMsg = ex is HttpClientException hce
                    ? $"HTTP {(int)hce.StatusCode} ({hce.StatusCode})"
                    : ex.Message;

                chunkResults.Add(new ChunkResult(
                    chunkInfo.ChunkId, "document", false, errorMsg));
                failedChunks.Add(chunkInfo.ChunkId);
            }

            // Progress indicator — every 10 chunks or at the end
            if ((i + 1) % 10 == 0 || i == chunks.Count - 1)
            {
                PrintProgress(inputPath, chunkResults.Count, 
                    $"Processed: {chunkResults.Count}/{chunks.Count}", ConsoleColor.Gray);
            }
        }

        stopwatch.Stop();

        var reportedDim = _detectedDim ?? 0;

        PrintProgress(inputPath, 0, "Results", ConsoleColor.Green);
        Console.WriteLine($"  Chunks processed: {chunkResults.Count}");
        Console.WriteLine($"  Successful:         {chunkResults.Count(r => r.Success)}");
        Console.WriteLine($"  Failed:             {failedChunks.Count}");
        if (failedChunks.Any())
            foreach (var fc in failedChunks)
                Console.WriteLine($"    - {fc}");
        Console.WriteLine($"  Embedding dimension: {reportedDim}");
        Console.WriteLine($"  Time elapsed:        {stopwatch.Elapsed.TotalSeconds:F1}s");

        return new ProcessResult(
            rootNode,
            inputPath,
            chunkResults,
            failedChunks.Count);
    }

    /// <summary>
    /// Writes the processed result to an output file.
    /// </summary>
    public static void WriteOutput(ProcessResult result, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null, // Preserve original property names
        };

        var jsonStr = result.RootNode.ToJsonString(options);
        File.WriteAllText(outputPath, jsonStr);
    }

    #region Private helpers

    private record ChunkInfo(string ChunkId, JsonNode ChunkNode, string? ContentKey);

    /// <summary>
    /// Safely extracts a string value from an JsonObject property.
    /// </summary>
    private static string? GetValueSafe(JsonObject obj, string propertyName)
    {
        var node = obj[propertyName];
        if (node is JsonValue jv && jv.TryGetValue<string>(out var str))
            return str;
        return null;
    }

    private List<ChunkInfo> ExtractChunks(JsonNode rootNode)
    {
        // First attempt: look for a 'chunks' array at root level
        if (rootNode is JsonObject obj && 
            obj["chunks"] is JsonArray chunkArray)
        {
            return chunkArray.OfType<JsonObject>()
                .Where(co => co["id"] != null || co.Count > 0)
                .Select((co, idx) => new ChunkInfo(
                    GetValueSafe(co, "id") ?? 
                        $"chunk-{idx}",
                    co,
                    ExtractContentKey(co)))
                .ToList();
        }

        // Fallback: look for any array that might contain chunks
        foreach (var kvp in rootNode.AsObject())
        {
            if (kvp.Value is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject firstObj)
            {
                return arr.OfType<JsonObject>()
                    .Select((co, idx) => new ChunkInfo(
                        GetValueSafe(co, "id") ?? 
                            $"chunk-{idx}",
                        co,
                        ExtractContentKey(co)))
                    .ToList();
            }
        }

        return new List<ChunkInfo>();
    }

    private static string? ExtractContentKey(JsonObject chunk)
    {
        // The actual document field — typically "document", but detect it
        var candidates = new[] { "document", "text", "body", "data" };
        foreach (var candidate in candidates)
        {
            if (chunk.ContainsKey(candidate))
                return candidate;
        }

        // Last resort: find any string-valued property that looks like content text
        foreach (var prop in chunk)
        {
            if (prop.Value is JsonValue jv && 
                jv.TryGetValue<string>(out var strVal) &&
                !string.IsNullOrEmpty(strVal) && 
                strVal.Length > 20) // Likely content, not metadata like id or index
            {
                return prop.Key;
            }
        }

        return null;
    }

    private static string ExtractText(ChunkInfo chunkInfo)
    {
        var key = chunkInfo.ContentKey ?? "document";
        
        if (chunkInfo.ChunkNode[key] is JsonValue jv && 
            jv.TryGetValue<string>(out var text))
        {
            return text;
        }

        // Try to convert via serialization as fallback
        var serialized = chunkInfo.ChunkNode[key]?.ToJsonString();
        if (!string.IsNullOrEmpty(serialized))
            return serialized;

        throw new InvalidOperationException(
            $"Chunk '{chunkInfo.ChunkId}' has no extractable text content.");
    }

    private static QdrantPoint BuildQdrantPoint(ChunkInfo chunkInfo, float[] vector)
    {
        var pointId = chunkInfo.ChunkId;

        // Build payload from the original chunk's metadata + id
        var payload = new JsonObject
        {
            ["id"] = JsonValue.Create(pointId),
        };

        // Copy all properties from the original chunk into the payload 
        // (except "points" which is what we're building)
        foreach (var prop in chunkInfo.ChunkNode.AsObject())
        {
            if (prop.Key == "points") continue;
            payload[prop.Key] = prop.Value.DeepClone();
        }

        return new QdrantPoint(pointId, vector, payload);
    }

    private static JsonObject BuildPointJson(QdrantPoint point, string? vectorName)
    {
        var obj = new JsonObject();
        obj["id"] = JsonValue.Create(point.Id);

        if (!string.IsNullOrEmpty(vectorName))
        {
            // Named-vector format: {"vector-name": [...]}
            var vecObj = new JsonObject();
            var vecArr = new JsonArray();
            foreach (var v in (float[])point.Vector)
                vecArr.Add(v);
            vecObj[vectorName] = vecArr;
            obj["vector"] = vecObj;
        }
        else
        {
            // Legacy raw-array format: "vector": [...]
            var vecArr = new JsonArray();
            foreach (var v in (float[])point.Vector)
                vecArr.Add(v);
            obj["vector"] = vecArr;
        }

        obj["payload"] = point.Payload.DeepClone() as JsonObject ?? new JsonObject();

        return obj;
    }

    private void PrintProgress(string inputPath, int processed, string label, ConsoleColor color)
    {
        Console.ResetColor();
        if (label != "Input" && label != "Results")
            return;

        Console.ForegroundColor = color;
        Console.WriteLine($"  {label}");
        Console.ResetColor();
        Console.Write("  Input:   ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(inputPath);

        if (label == "Results")
        {
            try 
            { 
                var dim = _embeddingService.EmbeddingDimension;
                Console.ResetColor();
                Console.Write("  Dimension: ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(dim);
            }
            catch { /* dimension not detected — skip */ }
        }

        Console.ResetColor();
    }

    #endregion
}

/// <summary>
/// Result of a full processing run.
/// </summary>
public record ProcessResult(
    JsonNode RootNode,
    string InputPath,
    IReadOnlyList<EmbeddingProcessor.ChunkResult> Results,
    int FailedCount);
