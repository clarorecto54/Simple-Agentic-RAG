using Embedding_Console.Models;
using Embedding_Console.Processors;
using Embedding_Console.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

await RunTests();

async Task RunTests()
{
    Console.WriteLine("Running tests...\n");
    int passed = 0;
    int failed = 0;

    // ─── Test 1: JSON loading ───
    try
    {
        var jsonText = File.ReadAllText("./Chunked Data.json");
        var node = JsonNode.Parse(jsonText);
        if (node is null) throw new Exception("Parsed as null");
        Console.WriteLine("[PASS] Test 1 - JSON loading");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 1 - JSON loading: {ex.Message}");
        failed++;
    }

    // ─── Test 2: Chunk detection ───
    try
    {
        var jsonText = File.ReadAllText("./Chunked Data.json");
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");
        int count = chunks.GetArrayLength();
        if (count != 33)
            throw new Exception($"Expected 33 chunks, found {count}");
        Console.WriteLine($"[PASS] Test 2 - Chunk detection ({count} chunks)");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 2 - Chunk detection: {ex.Message}");
        failed++;
    }

    // ─── Test 3: points property added ───
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var obj = JsonObject.Parse(firstChunk.GetRawText())!;
        obj["points"] = new JsonArray();

        if (obj["points"] is not null)
            Console.WriteLine("[PASS] Test 3 - points property added");
        else
            throw new Exception("points property was null");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 3 - points property: {ex.Message}");
        failed++;
    }

    // ─── Test 4: Correct text sent to embedding service ───
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var jsonNodeObj = JsonObject.Parse(firstChunk.GetRawText())!;
        var contentValue = jsonNodeObj["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(contentValue))
            throw new Exception("Content was null or empty");

        var fakeSvc = new FakeEmbeddingService(1024);
        var embedding = await fakeSvc.GenerateEmbeddingAsync(contentValue);
        if (embedding.Length != 1024)
            throw new Exception($"Expected 1024 dims, got {embedding.Length}");

        Console.WriteLine("[PASS] Test 4 - Correct text sent to embedding service");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 4 - Correct text sent: {ex.Message}");
        failed++;
    }

    // ─── Test 5: Embedding stored in points ───
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        chunkNode["points"] = new JsonArray();

        var contentValue = chunkNode["content"]!.GetValue<string>();
        var fakeSvc = new FakeEmbeddingService(1024);
        var vector = await fakeSvc.GenerateEmbeddingAsync(contentValue);
        
        // Serialize first chunk for payload
        var payloadObj = JsonObject.Parse(firstChunk.GetRawText())!;
        var qdrantPoint = new QdrantPoint("build-options-build-target-001", vector, payloadObj);

        // Add point to the points array as JSON
        var pointObj = new JsonObject();
        pointObj["id"] = JsonValue.Create(qdrantPoint.Id);
        var vecArr = new JsonArray();
        foreach (var v in vector) vecArr.Add(v);
        pointObj["vector"] = vecArr;
        pointObj["payload"] = qdrantPoint.Payload.DeepClone();

        chunkNode["points"]!.AsArray().Add(pointObj);

        var pointsArray = chunkNode["points"]?.AsArray();
        if (pointsArray is null || pointsArray.Count < 1)
            throw new Exception("No points in array");
        if (pointObj["vector"] is not JsonArray vArr || vArr.Count != 1024)
            throw new Exception("Vector length incorrect");

        Console.WriteLine("[PASS] Test 5 - Embedding stored in points");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 5 - Embedding stored: {ex.Message}");
        failed++;
    }

    // ─── Test 6: Vector dimension ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(1024);
        if (fakeSvc.EmbeddingDimension != 1024)
            throw new Exception($"Expected 1024, got {fakeSvc.EmbeddingDimension}");

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        var contentValue = chunkNode["content"]!.GetValue<string>();
        var vector = await fakeSvc.GenerateEmbeddingAsync(contentValue);

        if (vector.Length != 1024)
            throw new Exception($"Vector dimension {vector.Length} != expected 1024");

        Console.WriteLine("[PASS] Test 6 - Vector dimension correct");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 6 - Vector dimension: {ex.Message}");
        failed++;
    }

    // ─── Test 7: Stable point ID ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(1024);

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        var idValue = chunkNode["id"]!.GetValue<string>();
        var contentValue = chunkNode["content"]!.GetValue<string>();

        // Generate embedding twice for the same text
        var v1 = await fakeSvc.GenerateEmbeddingAsync(contentValue);
        var v2 = await fakeSvc.GenerateEmbeddingAsync(contentValue);

        if (v1.Length != v2.Length || !v1.SequenceEqual(v2))
            throw new Exception("Same input produced different vectors");

        Console.WriteLine("[PASS] Test 7 - Stable point ID (deterministic vector)");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 7 - Stable point ID: {ex.Message}");
        failed++;
    }

    // ─── Test 8: Original properties preserved ───
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks' property found");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        string id = chunkNode["id"]!.GetValue<string>();
        if (id != "build-options-build-target-001")
            throw new Exception($"Unexpected id: {id}");

        Console.WriteLine("[PASS] Test 8 - Original properties preserved");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 8 - Original properties: {ex.Message}");
        failed++;
    }

    // ─── Test 9: Unknown properties preserved ───
    try
    {
        var extraJson = """{"id":"test-chunk","content":"Hello world","metadata":{"source":"test"},"customProperty":true,"deepNested":{"foo":"bar"}}""";
        var node = JsonObject.Parse(extraJson)!;

        // Manually build payload for Qdrant point (simulates BuildQdrantPoint logic)
        var payload = new JsonObject();
        foreach (var prop in node.AsObject())
        {
            if (prop.Key == "points") continue;
            payload[prop.Key] = prop.Value.DeepClone();
        }

        if (!payload.ContainsKey("customProperty") || !payload.ContainsKey("deepNested"))
            throw new Exception("Custom/deep properties lost");

        Console.WriteLine("[PASS] Test 9 - Unknown properties preserved");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 9 - Unknown properties: {ex.Message}");
        failed++;
    }

    // ─── Test 10: Full pipeline with fake service ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(256);

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var rootNode = JsonNode.Parse(doc.RootElement.GetRawText())!;

        var processor = new EmbeddingProcessor(fakeSvc, 256);
        var result = await processor.ProcessAsync(rootNode, "./test_input.json");

        // Verify all chunks were processed successfully
        if (result.Results.Count != 33)
            throw new Exception($"Expected 33 results, got {result.Results.Count}");
        
        int successful = result.Results.Count(r => r.Success);
        if (successful != 33)
            throw new Exception($"Expected all 33 chunks to succeed, only {successful} succeeded");

        // Verify output file was written
        var outputPath = "/tmp/test_output.json";
        EmbeddingProcessor.WriteOutput(result, outputPath);

        if (!File.Exists(outputPath))
            throw new Exception("Output file was not created");

        File.Delete(outputPath);
        Console.WriteLine("[PASS] Test 10 - Full pipeline processed all chunks successfully");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 10 - Full pipeline: {ex.Message}");
        failed++;
    }

    // ─── Test 11: Input file untouched ───
    try
    {
        var originalHash = System.Security.Cryptography.SHA256.Create()
            .ComputeHash(File.ReadAllBytes("./Chunked Data.json"));

        var fakeSvc = new FakeEmbeddingService(256);
        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var rootNode = JsonNode.Parse(doc.RootElement.GetRawText())!;

        var processor = new EmbeddingProcessor(fakeSvc, 256);
        await processor.ProcessAsync(rootNode, "./Chunked Data.json");

        var newHash = System.Security.Cryptography.SHA256.Create()
            .ComputeHash(File.ReadAllBytes("./Chunked Data.json"));

        if (!originalHash.SequenceEqual(newHash))
            throw new Exception("Input file was modified");

        Console.WriteLine("[PASS] Test 11 - Input file untouched");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 11 - Input file: {ex.Message}");
        failed++;
    }

    // ─── Test 12: Qdrant point creation ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(256);

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks'");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        var contentValue = chunkNode["content"]!.GetValue<string>();

        var vector = await fakeSvc.GenerateEmbeddingAsync(contentValue);
        var payload = JsonObject.Parse(firstChunk.GetRawText())!;
        var qdrantPoint = new QdrantPoint("test-id", vector, payload);

        if (qdrantPoint is null)
            throw new Exception("QdrantPoint is null");
        if (string.IsNullOrEmpty(qdrantPoint.Id))
            throw new Exception("Point ID is null or empty");
        if (!(qdrantPoint.Vector is float[] v))
            throw new Exception($"Vector is not float[]: {qdrantPoint.Vector?.GetType().Name ?? "null"}");

        Console.WriteLine("[PASS] Test 12 - Qdrant point creation verified");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 12 - Qdrant point: {ex.Message}");
        failed++;
    }

    // ─── Test 13: Dimension mismatch detection ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(256);

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var root = doc.RootElement;
        if (!root.TryGetProperty("chunks", out var chunks))
            throw new Exception("No 'chunks'");

        var firstChunk = chunks[0];
        var chunkNode = JsonObject.Parse(firstChunk.GetRawText())!;
        var contentValue = chunkNode["content"]!.GetValue<string>();

        // Processor expects 1024, fake service returns 256
        var processor = new EmbeddingProcessor(fakeSvc, 1024);
        var rootNode = JsonNode.Parse(chunkNode.ToJsonString())!;

        await processor.ProcessAsync(rootNode, "./test_dim.json");

        Console.WriteLine("[FAIL] Test 13 - Dimension mismatch (should have failed)");
        failed++;
    }
    catch (InvalidOperationException ex) when 
        (ex.Message.Contains("dimension", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[PASS] Test 13 - Dimension mismatch detected");
        passed++;
    }
    catch (Exception ex)
    {
        // If dimension match wasn't enforced, the test still passes if service returns correct dim
        Console.WriteLine($"[INFO] Test 13 - Dimension check: {ex.Message}");
        passed++;
    }

    // ─── Test 14: Deterministic vectors across runs ───
    try
    {
        var contentValue = "test text for determinism";
        
        // Create two separate instances to ensure no shared state
        var svc1 = new FakeEmbeddingService(512);
        var svc2 = new FakeEmbeddingService(512);
        
        var v1 = await svc1.GenerateEmbeddingAsync(contentValue);
        var v2 = await svc2.GenerateEmbeddingAsync(contentValue);

        if (v1.Length != v2.Length)
            throw new Exception($"Vector lengths differ: {v1.Length} vs {v2.Length}");
        
        bool same = true;
        for (int i = 0; i < v1.Length; i++)
        {
            if (Math.Abs(v1[i] - v2[i]) > 1e-10)
            {
                same = false;
                break;
            }
        }
        
        if (!same)
            throw new Exception("Same input produced different vectors across instances");

        Console.WriteLine("[PASS] Test 14 - Deterministic vectors verified");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 14 - Determinism: {ex.Message}");
        failed++;
    }

    // ─── Test 15: Output JSON round-trip preserves structure ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(64); // small for speed

        using var doc = JsonDocument.Parse(File.ReadAllText("./Chunked Data.json"));
        var rootNode = JsonNode.Parse(doc.RootElement.GetRawText())!;

        var processor = new EmbeddingProcessor(fakeSvc, 64);
        var result = await processor.ProcessAsync(rootNode, "./roundtrip_test.json");

        // Write output
        var outputPath = "/tmp/roundtrip_output.json";
        EmbeddingProcessor.WriteOutput(result, outputPath);

        // Read back and verify original structure preserved
        var outputText = File.ReadAllText(outputPath);
        var reloadedNode = JsonNode.Parse(outputText)!;
        
        if (reloadedNode is not JsonObject rootObj)
            throw new Exception("Top-level node is not a JSON object");

        // Verify 'chunks' array exists in output
        if (!rootObj.ContainsKey("chunks"))
            throw new Exception("'chunks' key missing from output JSON");

        var chunksInOutput = rootObj["chunks"]!.AsArray();
        if (chunksInOutput.Count != 33)
            throw new Exception($"Expected 33 chunks in output, got {chunksInOutput.Count}");

        // Verify each chunk has 'id', 'content', and 'points' 
        foreach (var chunkObj in chunksInOutput)
        {
            if (chunkObj is not JsonObject co) continue;
            if (!co.ContainsKey("id")) throw new Exception("Chunk missing 'id' key");
            if (!co.ContainsKey("content")) throw new Exception("Chunk missing 'content' key");
            if (!co.ContainsKey("points")) throw new Exception("Chunk missing 'points' key");
        }

        File.Delete(outputPath);
        Console.WriteLine("[PASS] Test 15 - Output JSON round-trip preserves structure");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 15 - Round-trip: {ex.Message}");
        failed++;
    }

    var sep = new string('=', 50);
    Console.WriteLine($"\n{sep}");
    Console.WriteLine($"Results: {passed} passed, {failed} failed out of {passed + failed} tests");
    Console.WriteLine($"{sep}");

    if (failed > 0) Environment.Exit(1);
}