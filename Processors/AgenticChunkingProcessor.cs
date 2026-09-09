namespace Embedding_Console.Processors;

using Embedding_Console.Services;
using Embedding_Console.Utils;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Orchestrates the 3-stage agentic chunking pipeline:
///   Stage 1 (Segment) → raw markdown → segmentation plan + extracted segments
///   Stage 2 (Semantic) → each segment → semantic analysis with metadata
///   Stage 3 (Chunking) → each semantically analyzed section → final RAG-ready chunks
/// Each stage calls the LLM separately, resetting context to avoid rotting.
/// </summary>
public class AgenticChunkingProcessor : IDisposable
{
    private readonly IRagService _ragService;
    private readonly string _promptDir;
    private readonly int _maxChunkTokens;
    private readonly string _outputDir;
    private readonly TimeSpan _llamaTimeout;

    public AgenticChunkingProcessor(
        IRagService ragService,
        string promptDir,
        int maxChunkTokens = 8000,
        string outputDir = "./rag_output",
        TimeSpan? llamaTimeout = null)
    {
        _ragService = ragService;
        _promptDir = promptDir;
        _maxChunkTokens = maxChunkTokens;
        _outputDir = outputDir;
        _llamaTimeout = llamaTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Processes a single markdown file through all three stages.
    /// </summary>
    public async Task<RagingResult> ProcessFileAsync(
        string inputFilePath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sourceFileName = Path.GetFileNameWithoutExtension(inputFilePath);

        // Initialize per-file logging for this run
        PipelineLogger.Clear();
        PipelineLogger.WriteEntry(sourceFileName, "INFO", $"Processing started: {inputFilePath}");

        Console.WriteLine();
        Console.WriteLine($"\u001b[36m=== Agentic Chunking: {sourceFileName} ===\u001b[0m");

        // Read and chunk the raw markdown by token budget if needed
        var rawMarkdown = File.ReadAllText(inputFilePath);
        int charCount = rawMarkdown.Length;
        Console.WriteLine($"  Input:    {inputFilePath} ({charCount:N0} chars)");

        // Log raw input for debugging
        PipelineLogger.WriteInput(sourceFileName, "RAW_INPUT", rawMarkdown);

        // Estimate tokens (~4 chars per token for markdown with code blocks)
        int estimatedTokens = charCount / 4;
        List<(string index, string content)> markdownBatches;
        string batchKeyPrefix = sourceFileName.ToLower().Replace(" ", "-").Replace(".", "");

        if (estimatedTokens > _maxChunkTokens)
        {
            Console.WriteLine($"  Chunking markdown into ~{_maxChunkTokens} token batches...");
            PipelineLogger.WriteEntry(sourceFileName, "INFO", $"Chunked into {_maxChunkTokens} token batches");
            markdownBatches = ChunkMarkdown(rawMarkdown, _maxChunkTokens);
            // Prepend a note so the LLM knows we're processing in parts
            for (int i = 0; i < markdownBatches.Count; i++)
            {
                markdownBatches[i] = (markdownBatches[i].index,
                    $"[Document Part {i + 1}/{markdownBatches.Count}]\n\n{markdownBatches[i].content}");
            }
        }
        else
        {
            markdownBatches = new[] { ("0", rawMarkdown) }.ToList();
        }

        // ===== STAGE 1: Segment =====
        Console.WriteLine("\n\u001b[33m  → Stage 1: Document Segmentation\u001b[0m");
        var allSegmentedFiles = new List<SegmentedFile>();
        foreach (var batch in markdownBatches)
        {
            string stage1Prompt = LoadPrompt("01 Segment.md");
            PipelineLogger.WriteInput(sourceFileName, "STAGE1_PROMPT", stage1Prompt);
            PipelineLogger.WriteInput(sourceFileName, $"STAGE1_INPUT_batch{batch.index}", batch.content);

            string stage1Result;
            try
            {
                stage1Result = await RunStageAsync(
                    "01 Segment",
                    stage1Prompt,
                    batch.content,
                    cancellationToken);
                PipelineLogger.WriteOutput(sourceFileName, "STAGE1_OUTPUT", stage1Result);

                var segments = ParseStage1Output(stage1Result, batch.index, sourceFileName);
                // Extract source_content for each segment from the batch content
                foreach (var s in segments)
                {
                    var seg = s with { SourceContent = ExtractSegmentContent(batch.content, s) };
                    allSegmentedFiles.Add(seg);
                }

                PipelineLogger.WriteEntry(sourceFileName, "STAGE1_INFO", $"Parsed {segments.Count} segments from batch {batch.index}");
            }
            catch (Exception ex) when (!(ex is AgenticChunkingException))
            {
                PipelineLogger.WriteError(sourceFileName, "STAGE1", ex, $"Batch index: {batch.index}\nContent length: {batch.content.Length} chars");
                Console.WriteLine($"\u001b[31m  ✗ Stage 1 batch {batch.index}: {ex.Message}\u001b[0m");
            }
        }

        PipelineLogger.WriteEntry(sourceFileName, "STAGE1_SUMMARY", $"Total segments: {allSegmentedFiles.Count}");
        foreach (var seg in allSegmentedFiles)
        {
            PipelineLogger.WriteEntry(sourceFileName, "SEGMENT_DETAIL", $"{seg.Id}: \"{seg.Title}\" ({seg.SourceContent.Length} chars)");
        }

        Console.WriteLine($"  Segments: {allSegmentedFiles.Count}");

        // ===== STAGE 2: Semantic Extraction =====
        Console.WriteLine("\n\u001b[33m  → Stage 2: Semantic Extraction\u001b[0m");
        var semanticResults = new List<(string segmentId, string markdownContent, JsonObject analysis)>();
        for (int i = 0; i < allSegmentedFiles.Count; i++)
        {
            var seg = allSegmentedFiles[i];
            Console.Write($"  [{i + 1}/{allSegmentedFiles.Count}] Processing: {seg.Title}...");

            string stage2Prompt = LoadPrompt("02 Semantic.md");
            PipelineLogger.WriteInput(sourceFileName, $"STAGE2_INPUT_seg{seg.Id}", seg.SourceContent);

            string stage2Result;
            try
            {
                stage2Result = await RunStageAsync(
                    "02 Semantic",
                    stage2Prompt,
                    seg.SourceContent,
                    cancellationToken);
                PipelineLogger.WriteOutput(sourceFileName, $"STAGE2_OUTPUT_seg{seg.Id}", stage2Result);

                var analysis = ParseStage2Output(stage2Result);
                semanticResults.Add((seg.Id, seg.SourceContent, analysis));
                PipelineLogger.WriteEntry(sourceFileName, "STAGE2_SUCCESS", $"Parsed analysis for {seg.Id}");
                Console.WriteLine("\u001b[32m✓\u001b[0m");
            }
            catch (Exception ex)
            {
                PipelineLogger.WriteError(sourceFileName, $"STAGE2_seg{seg.Id}", ex, $"Segment title: {seg.Title}\nSource content length: {seg.SourceContent?.Length ?? 0} chars");
                Console.WriteLine($"\u001b[31m✗ ({ex.Message})\u001b[0m");
            }
        }

        int stage2Chunks = semanticResults.Sum(sr => GetChunkCount(sr.analysis));
        PipelineLogger.WriteEntry(sourceFileName, "STAGE2_SUMMARY", $"Semantic chunks produced: {stage2Chunks}");

        // ===== STAGE 3: Final Chunking =====
        Console.WriteLine("\n\u001b[33m  → Stage 3: RAG Chunking\u001b[0m");
        var allFinalChunks = new List<JsonObject>();

        for (int i = 0; i < semanticResults.Count; i++)
        {
            var sr = semanticResults[i];
            int processed = 0;
            try
            {
                // The Stage 2 output has a "chunks" array — each is one section to pass to Stage 3
                if (sr.analysis["chunks"] is JsonArray stage2ChunksArr)
                {
                    foreach (var chunkNode in stage2ChunksArr)
                    {
                        if (chunkNode is not JsonObject chunkObj) continue;
                        processed++;

                        string sourceMd = chunkObj["source_content"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(sourceMd)) continue;

                        // Build the input for Stage 3: include document context + section metadata
                        var stage3Input = BuildStage3Input(
                            sourceFileName,
                            sr.segmentId,
                            chunkObj);

                        PipelineLogger.WriteInput(sourceFileName, $"STAGE3_INPUT_seg{sr.segmentId}_chunk{processed}", stage3Input);

                        string stage3Result;
                        try
                        {
                            stage3Result = await RunStageAsync(
                                "03 Chunking",
                                LoadPrompt("03 Chunking.md"),
                                stage3Input,
                                cancellationToken);
                            PipelineLogger.WriteOutput(sourceFileName, $"STAGE3_OUTPUT_seg{sr.segmentId}", stage3Result);
                        }
                        catch (Exception ex)
                        {
                            PipelineLogger.WriteError(sourceFileName, $"STAGE3_run_seg{sr.segmentId}_chunk{processed}", ex, $"Source content length: {sourceMd.Length}");
                            throw;
                        }

                        var chunks = ParseStage3Output(stage3Result, sourceFileName, sr.segmentId);
                        allFinalChunks.AddRange(chunks);

                        if (processed % 5 == 0)
                        {
                            Console.WriteLine($"  Processed {processed} semantic sections, {allFinalChunks.Count} final chunks so far...");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PipelineLogger.WriteError(sourceFileName, $"STAGE3_seg{sr.segmentId}", ex, $"Semantic chunk count from segment: {GetChunkCount(sr.analysis)}");
                Console.WriteLine($"\u001b[31m✗ Segment {sr.segmentId}: {ex.Message}\u001b[0m");
            }

            Console.WriteLine($"\u001b[36m  [{i + 1}/{semanticResults.Count}] Semantic chunking done\u001b[0m");
        }

        PipelineLogger.WriteEntry(sourceFileName, "STAGE3_SUMMARY", $"Final chunks: {allFinalChunks.Count}");

        int stage3Final = allFinalChunks.Count;

        // ===== Write output =====
        string safeName = sourceFileName.ToLower().Replace(" ", "-").Replace(".", "");
        Directory.CreateDirectory(_outputDir);
        var outputFile = Path.Combine(_outputDir, $"{safeName}.ragged.json");

        var outputObj = new JsonObject
        {
            ["source_file"] = inputFilePath,
            ["document_title"] = sourceFileName,
            ["stage1_segments"] = allSegmentedFiles.Count,
            ["stage2_semantic_chunks"] = stage2Chunks,
            ["stage3_final_chunks"] = stage3Final,
            ["processing_time_seconds"] = stopwatch.Elapsed.TotalSeconds,
            ["chunks"] = new JsonArray(),
        };

        // Wrap each final chunk in the expected structure matching Chunked Data.json format
        foreach (var chunkObj in allFinalChunks)
        {
            var wrapped = new JsonObject();
            if (chunkObj["id"] is JsonNode idNode)
                wrapped["id"] = idNode.DeepClone();
            else
                wrapped["id"] = JsonValue.Create($"{safeName}-chunk-{outputObj["chunks"]!.AsArray().Count + 1:D3}");

            if (chunkObj["content"] is JsonNode contentNode)
                wrapped["content"] = contentNode.DeepClone();
            else
                wrapped["content"] = JsonValue.Create("");

            if (chunkObj["metadata"] is JsonObject metadataObj)
                wrapped["metadata"] = metadataObj.DeepClone() as JsonObject ?? new JsonObject();
            else
                wrapped["metadata"] = new JsonObject();

            outputObj["chunks"]!.AsArray().Add(wrapped);
        }

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null };
        File.WriteAllText(outputFile, outputObj.ToJsonString(options));

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"\u001b[32m  Output:    {outputFile}\u001b[0m");
        Console.WriteLine($"  Final chunks: {stage3Final}");
        Console.WriteLine($"  Time:          {stopwatch.Elapsed.TotalSeconds:F1}s");

        // Flush per-file log to disk
        PipelineLogger.FlushToFile(sourceFileName, _outputDir);

        return new RagingResult(
            inputFilePath,
            outputFile,
            allSegmentedFiles.Count,
            stage2Chunks,
            stage3Final,
            stopwatch.Elapsed);
    }

    #region Stage helpers

    private async Task<string> RunStageAsync(string stageName, string promptTemplate, string markdownContent, CancellationToken ct)
    {
        // Build the full prompt: template + input instruction + markdown content
        var fullPrompt = $"{promptTemplate}\n\n---\n\n# SOURCE MARKDOWN\n\n```\n{markdownContent}\n```\n\nReturn ONLY valid JSON.";

        string result;
        try
        {
            // Use a timeout wrapper since RagService may not support cancellation well internally
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_llamaTimeout);
            result = await _ragService.SendAsync(fullPrompt).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new AgenticChunkingException($"{stageName} timed out after {_llamaTimeout.TotalMinutes:F0}m.");
        }

        // Strip markdown code fences if present
        result = SanitizeJsonOutput(result);

        return result;
    }

    private List<SegmentedFile> ParseStage1Output(string jsonText, string batchIndex, string sourceFileName)
    {
        try
        {
            var doc = JsonNode.Parse(jsonText)!;

            // LLM may return either { "segments": [...] } or a bare array of segment objects
            JsonArray segmentsArr;
            if (doc is JsonObject rootObj && rootObj.ContainsKey("segments"))
                segmentsArr = rootObj["segments"]!.AsArray();
            else if (doc is JsonArray arr)
                segmentsArr = arr;
            else
                throw new AgenticChunkingException(
                    $"Stage 1 returned unexpected type: expected {{\"segments\": [...]}} or bare array, got {doc?.GetValueKind()}");

            var result = new List<SegmentedFile>();

            for (int i = 0; i < segmentsArr.Count; i++)
            {
                if (segmentsArr[i] is not JsonObject seg) continue;

                string id = GetStringValue(seg, "id") ?? $"seg-{batchIndex}-{i:D3}";
                string filename = GetStringValue(seg, "filename") ?? $"segment-{i}.md";
                string title = GetStringValue(seg, "title") ?? $"Segment {i + 1}";
                var headingPath = new List<string>();
                if (seg["heading_path"] is JsonArray hp)
                    foreach (var h in hp)
                        if (h is JsonValue hv && hv.TryGetValue<string>(out var hs))
                            headingPath.Add(hs);

                result.Add(new SegmentedFile(id, filename, title, headingPath, ""));
            }

            // If no valid segment objects were parsed but the LLM returned elements
            // (e.g., a bare array of heading strings), create one synthetic segment
            // so the file isn't silently skipped.
            if (result.Count == 0 && segmentsArr.Count > 0)
            {
                result.Add(new SegmentedFile(
                    $"seg-{batchIndex}-000",
                    "fallback-segment.md",
                    sourceFileName,
                    new List<string>(),
                    ""));
            }

            return result;
        }
        catch (Exception ex) when (!(ex is AgenticChunkingException))
        {
            throw new AgenticChunkingException($"Stage 1 parse error: {ex.Message}\n\nRaw:\n{jsonText[..Math.Min(500, jsonText.Length)]}");
        }
    }

    private string ExtractSegmentContent(string sourceMarkdown, SegmentedFile segment)
    {
        // Look for the segment's title as a heading marker in the source markdown
        int titleIdx = sourceMarkdown.IndexOf($"## {segment.Title}", StringComparison.Ordinal);
        if (titleIdx < 0)
            titleIdx = sourceMarkdown.IndexOf($"# {segment.Title}", StringComparison.Ordinal);

        if (titleIdx >= 0)
        {
            // Find the next heading at same or lower level
            int nextH2 = FindNextHeading(sourceMarkdown, "## ", titleIdx);
            if (nextH2 < 0)
                return sourceMarkdown.Substring(titleIdx); // rest of file from this heading

            return sourceMarkdown.Substring(titleIdx, nextH2 - titleIdx);
        }

        // Fallback: truncate to max chunk size
        return sourceMarkdown.Length > _maxChunkTokens ? sourceMarkdown[.._maxChunkTokens] : sourceMarkdown;
    }

    private static int FindNextHeading(string text, string marker, int offset)
    {
        int idx = text.IndexOf(marker, offset + marker.Length);
        if (idx < 0) return -1;
        // Make sure it's a real heading (followed by non-empty content or end of line)
        int endOfLine = text.IndexOf('\n', idx);
        if (endOfLine > idx && endOfLine < idx + marker.Length + 200)
            return idx;
        // Try next occurrence
        return FindNextHeading(text, marker, idx + 1);
    }

    private static JsonObject ParseStage2Output(string jsonText)
    {
        try
        {
            string cleaned = SanitizeJsonOutput(jsonText);
            var doc = JsonNode.Parse(cleaned)!;

            JsonObject root;
            if (doc is JsonObject obj && obj.ContainsKey("chunks"))
                root = obj;
            else if (doc is JsonObject obj2)
            {
                // Object with a different top-level key — wrap it
                root = new JsonObject { ["chunks"] = doc };
            }
            else if (doc is JsonArray arr)
            {
                // LLM returned just the array (common fallback format)
                root = new JsonObject { ["chunks"] = arr };
            }
            else
                throw new AgenticChunkingException($"Stage 2 returned non-object: {typeof(JsonNode).Name}.");

            if (!root.ContainsKey("chunks"))
                throw new AgenticChunkingException("Stage 2 output missing 'chunks' array.");

            return root;
        }
        catch (Exception ex) when (!(ex is AgenticChunkingException))
        {
            throw new AgenticChunkingException($"Stage 2 parse error: {ex.Message}\n\nRaw:\n{jsonText[..Math.Min(500, jsonText.Length)]}");
        }
    }

    private static int GetChunkCount(JsonObject analysis)
    {
        try
        {
            return analysis["chunks"]?.AsArray().Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildStage3Input(
        string sourceFileName,
        string segmentId,
        JsonObject chunkObj)
    {
        // Build a structured prompt for Stage 3 with document context + section metadata + source content
        var sb = new StringBuilder();

        sb.AppendLine("You are processing one semantic section from a larger document. Chunk it for RAG retrieval.");
        sb.AppendLine();

        // Document-level context
        if (chunkObj["document"] is JsonObject docCtx)
        {
            sb.AppendLine("# DOCUMENT CONTEXT");
            if (docCtx.ContainsKey("title")) sb.AppendLine($"  Title: {docCtx["title"]!.GetValue<string>()}");
            if (docCtx.ContainsKey("source_file")) sb.AppendLine($"  Source: {docCtx["source_file"]!.GetValue<string>()}");
            if (docCtx.ContainsKey("document_type")) sb.AppendLine($"  Type: {docCtx["document_type"]!.GetValue<string>()}");
            if (docCtx.ContainsKey("primary_topic")) sb.AppendLine($"  Topic: {docCtx["primary_topic"]!.GetValue<string>()}");
            if (docCtx.ContainsKey("summary")) sb.AppendLine($"  Summary: {docCtx["summary"]!.GetValue<string>()}");
            sb.AppendLine();
        }

        // Section-level metadata
        sb.AppendLine("# SECTION METADATA");
        if (chunkObj.ContainsKey("heading_path"))
        {
            sb.Append("  Heading path: ");
            var parts = new List<string>();
            if (chunkObj["heading_path"] is JsonArray hp)
                foreach (var h in hp)
                    if (h is JsonValue hv && hv.TryGetValue<string>(out var hs))
                        parts.Add(hs);
            sb.AppendLine(string.Join(" → ", parts));
        }
        if (chunkObj.ContainsKey("topic"))
            sb.AppendLine($"  Topic: {chunkObj["topic"]!.GetValue<string>()}");
        if (chunkObj.ContainsKey("summary"))
            sb.AppendLine($"  Summary: {chunkObj["summary"]!.GetValue<string>()}");
        if (chunkObj.ContainsKey("keywords") && chunkObj["keywords"] is JsonArray kw)
            sb.Append($"  Keywords: {string.Join(", ", kw.OfType<JsonValue>().Select(j => j.GetValue<string>()))}").AppendLine();
        if (chunkObj.ContainsKey("entities") && chunkObj["entities"] is JsonArray ent)
            sb.Append($"  Entities: {string.Join(", ", ent.OfType<JsonValue>().Select(j => j.GetValue<string>()))}").AppendLine();
        if (chunkObj.ContainsKey("technologies") && chunkObj["technologies"] is JsonArray tech)
            sb.Append($"  Technologies: {string.Join(", ", tech.OfType<JsonValue>().Select(j => j.GetValue<string>()))}").AppendLine();
        sb.AppendLine();

        // Source content
        string sourceContent = "";
        if (chunkObj.ContainsKey("source_content"))
        {
            var scNode = chunkObj["source_content"];
            if (scNode is JsonValue scv && scv.TryGetValue<string>(out var scStr))
                sourceContent = scStr;
            else if (scNode is not null)
                sourceContent = scNode.ToJsonString();
        }

        sb.AppendLine("# SOURCE CONTENT");
        sb.AppendLine("````markdown");
        sb.AppendLine(sourceContent);
        sb.AppendLine("````");

        return sb.ToString();
    }

    private static List<JsonObject> ParseStage3Output(string jsonText, string sourceFileName, string segmentId)
    {
        try
        {
            string cleaned = SanitizeJsonOutput(jsonText);
            var doc = JsonNode.Parse(cleaned)!;

            // Stage 3 might return either a chunks array directly or an object with "chunks"
            JsonObject root;
            if (doc is JsonObject obj && obj.ContainsKey("chunks"))
                root = obj;
            else if (doc is JsonArray arr)
            {
                var wrapper = new JsonObject { ["chunks"] = arr };
                root = wrapper;
            }
            else
                throw new AgenticChunkingException($"Stage 3 returned unexpected type.");

            if (!root.ContainsKey("chunks"))
                throw new AgenticChunkingException("Stage 3 output missing 'chunks' array.");

            var chunks = new List<JsonObject>();
            foreach (var chunkNode in root["chunks"]!.AsArray())
            {
                if (chunkNode is not JsonObject chunk) continue;

                // Ensure the chunk has the expected shape for Chunked Data.json compatibility
                if (!chunk.ContainsKey("content"))
                    chunk["content"] = JsonValue.Create("");
                if (!chunk.ContainsKey("metadata"))
                    chunk["metadata"] = new JsonObject();

                // Fill in any missing metadata fields from segment context
                var meta = chunk["metadata"] as JsonObject ?? new JsonObject();
                if (!meta.ContainsKey("source_file") && !string.IsNullOrEmpty(sourceFileName))
                    meta["source_file"] = JsonValue.Create(sourceFileName);

                chunks.Add(chunk);
            }

            return chunks;
        }
        catch (Exception ex) when (!(ex is AgenticChunkingException))
        {
            throw new AgenticChunkingException($"Stage 3 parse error: {ex.Message}\n\nRaw:\n{jsonText[..Math.Min(500, jsonText.Length)]}");
        }
    }

    #endregion

    #region Markdown chunking by token budget

    private static List<(string index, string content)> ChunkMarkdown(string markdown, int maxTokens)
    {
        // Split at double-newline boundaries (paragraph level), trying to stay within token limits
        var paragraphs = SplitByParagraphs(markdown);
        var batches = new List<(int index, string content)>();
        var currentBatch = new StringBuilder();
        int currentTokens = 0;
        int batchIndex = 0;

        foreach (var para in paragraphs)
        {
            int paraTokens = EstimateTokenCount(para);

            // If a single paragraph exceeds the limit, split by lines within it
            if (paraTokens > maxTokens)
            {
                // Flush current batch if not empty
                if (currentBatch.Length > 0)
                {
                    batches.Add((batchIndex++, currentBatch.ToString()));
                    currentBatch.Clear();
                    currentTokens = 0;
                }

                // Split the long paragraph by newlines within it
                var lines = para.Split(new[] { "\n\n" }, StringSplitOptions.None);
                var subBatch = new StringBuilder();
                int subTokens = 0;

                foreach (var line in lines)
                {
                    int lt = EstimateTokenCount(line);
                    if (subTokens + lt > maxTokens && subBatch.Length > 0)
                    {
                        batches.Add((batchIndex++, subBatch.ToString()));
                        subBatch.Clear();
                        subTokens = 0;
                    }
                    subBatch.AppendLine(line);
                    subTokens += lt;
                }

                if (subBatch.Length > 0)
                {
                    batches.Add((batchIndex++, subBatch.ToString()));
                    batchIndex++;
                }
                continue;
            }

            // Try to add this paragraph to the current batch
            if (currentTokens + paraTokens > maxTokens && currentBatch.Length > 0)
            {
                batches.Add((batchIndex++, currentBatch.ToString()));
                currentBatch.Clear();
                currentTokens = 0;
            }

            currentBatch.AppendLine(para);
            currentTokens += paraTokens;
        }

        if (currentBatch.Length > 0)
            batches.Add((batchIndex, currentBatch.ToString()));

        return batches.Select(b => (b.index.ToString(), b.content)).ToList();
    }

    private static string[] SplitByParagraphs(string text)
    {
        // Split by double newlines but preserve some structure
        var parts = text.Split(new[] { "\n\n" }, StringSplitOptions.None);
        return parts.Where(p => !string.IsNullOrWhiteSpace(p.Trim())).ToArray();
    }

    private static int EstimateTokenCount(string text)
    {
        // Rough estimate: ~4 characters per token for markdown/code-heavy content
        // Adjust based on code blocks (lower ratio) vs plain text
        int codeBlockChars = CountCodeBlockChars(text);
        int nonCodeChars = text.Length - codeBlockChars;

        // Code is denser: ~6 chars per token. Plain text/markdown: ~4 chars per token.
        return (nonCodeChars / 4) + (codeBlockChars / 6);
    }

    private static int CountCodeBlockChars(string markdown)
    {
        int count = 0;
        bool inCodeBlock = false;
        string[] lines = markdown.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```"))
            {
                if (inCodeBlock)
                    count += trimmed.Length + 1; // include the closing fence
                inCodeBlock = !inCodeBlock;
            }
            else if (inCodeBlock)
            {
                count += line.Length + 1;
            }
        }

        return count;
    }

    #endregion

    #region Prompt loading (embedded resource + filesystem fallback)

    /// <summary>Cached embedded prompts loaded once at first use.</summary>
    private static readonly Dictionary<string, string> _embeddedPrompts = LoadEmbeddedPrompts();

    private static Dictionary<string, string> LoadEmbeddedPrompts()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(AgenticChunkingProcessor).Assembly;
            // Get the embedded resource stream by its logical name
            var resourceName = "Embedding_Console.Prompts.prompts.json";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return dict;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var doc = JsonNode.Parse(json);
            if (doc is JsonObject root)
            {
                foreach (var kvp in root)
                {
                    if (kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var promptText))
                    {
                        // Map short keys to canonical filenames
                        string key = kvp.Key.ToLowerInvariant();
                        if (key == "segment" || key.Contains("01") || key.Contains("segment"))
                            dict["01 Segment.md"] = promptText;
                        else if (key == "semantic" || key.Contains("02") || key.Contains("semantic"))
                            dict["02 Semantic.md"] = promptText;
                        else if (key == "chunking" || key.Contains("03") || key.Contains("chunking"))
                            dict["03 Chunking.md"] = promptText;
                    }
                }
            }
        }
        catch
        {
            // Silently ignore — filesystem fallback will apply
        }
        return dict;
    }

    private string LoadPrompt(string promptFilename)
    {
        // Try embedded resource first (case-insensitive lookup on canonical name)
        if (_embeddedPrompts.TryGetValue(promptFilename, out var embedded))
            return embedded;

        // Fallback: read from file system at custom prompt directory
        var promptPath = Path.Combine(_promptDir, $"[PROMPT] {promptFilename}");
        if (!File.Exists(promptPath))
            throw new AgenticChunkingException($"Prompt file not found: {promptPath}");

        return File.ReadAllText(promptPath);
    }

    private static string SanitizeJsonOutput(string raw)
    {
        // Remove markdown code fences (```) that LLMs sometimes add
        var trimmed = raw.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed.Substring(firstNewline + 1).Trim();
            }
        }

        // Remove trailing closing fence if present
        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            int lastFenceStart = trimmed.LastIndexOf("```");
            trimmed = trimmed.Substring(0, lastFenceStart).Trim();
        }

        return trimmed;
    }

    private static string? GetStringValue(JsonNode node, string key)
    {
        if (node is JsonObject obj && obj.ContainsKey(key) && obj[key] is JsonValue jv && jv.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    #endregion

    public void Dispose() { /* IRagService may be injected externally */ }
}

/// <summary>
/// Represents an error during the agentic chunking pipeline.
/// </summary>
public class AgenticChunkingException : Exception
{
    public AgenticChunkingException(string message) : base(message) { }
}
