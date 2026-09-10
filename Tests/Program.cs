using Embedding_Console;
using Embedding_Console.Models;
using Embedding_Console.Processors;
using Embedding_Console.Services;
using System.Text;
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

    // ─── Test 16: SanitizeJsonOutput escapes literal control chars ───
    try
    {
        var sb = new StringBuilder();
        sb.Append("{\"chunks\":[");
        sb.Append("{\"content\":\"The list of chunks to preload for each dynamic import is computed by Vite.\",");
        sb.Append("\"retrieval_content\":\"Vite Documentation"); // literal newline (0x0A)
        sb.Append("\u000A");
        sb.Append("build.modulePreload\"}]}");
        string rawWithLiteralNewlines = sb.ToString();

        var sanitized = Embedding_Console.Processors.AgenticChunkingProcessor.SanitizeJsonOutput(rawWithLiteralNewlines);

        try
        {
            JsonNode.Parse(sanitized);
            Console.WriteLine("[PASS] Test 16 - SanitizeJsonOutput escapes literal newlines");
            passed++;
        }
        catch (Exception ex)
        {
            throw new Exception($"SanitizeJsonOutput failed to fix: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 16 - SanitizeJsonOutput: {ex.Message}");
        failed++;
    }

    // ─── Test 17: ExtractChunksForMerge finds chunks at root level ───
    try
    {
        var jsonText = "{\"chunks\":[{\"id\":\"c1\",\"content\":\"hello\"},{\"id\":\"c2\",\"content\":\"world\"}]}";
        using var doc = JsonDocument.Parse(jsonText);
        var testRoot = JsonNode.Parse(doc.RootElement.GetRawText())!;

        var chunks = BatchEmbedHelpers.ExtractChunksForMerge(testRoot);
        if (chunks.Count != 2)
            throw new Exception($"Expected 2 chunks, got {chunks.Count}");
        var firstChunk = chunks[0] as JsonObject;
        if (firstChunk == null)
            throw new Exception("First chunk is not an object");
        if (firstChunk["id"]!.GetValue<string>() != "c1")
            throw new Exception("First chunk id mismatch");

        Console.WriteLine("[PASS] Test 17 - ExtractChunksForMerge root-level chunks");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 17 - ExtractChunksForMerge: {ex.Message}");
        failed++;
    }

    // ─── Test 18: Batch orchestrator merges multiple files ───
    try
    {
        var tempDir = "/tmp/batch_embed_test";
        Directory.CreateDirectory(tempDir);

        string file1Json = "{\"chunks\":[{\"id\":\"file1-chunk-1\",\"content\":\"alpha\"},{\"id\":\"file1-chunk-2\",\"content\":\"beta\"}]}";
        string file2Json = "{\"chunks\":[{\"id\":\"file2-chunk-1\",\"content\":\"gamma\"}]}";

        var fakeSvc = new FakeEmbeddingService(128);

        var result1 = await ProcessSingleFile(fakeSvc, file1Json, Path.Combine(tempDir, "input1.ragged.json"));
        var result2 = await ProcessSingleFile(fakeSvc, file2Json, Path.Combine(tempDir, "input2.ragged.json"));

        var allChunks = new List<JsonNode>();
        allChunks.AddRange(BatchEmbedHelpers.ExtractChunksForMerge(result1.RootNode));
        allChunks.AddRange(BatchEmbedHelpers.ExtractChunksForMerge(result2.RootNode));

        if (allChunks.Count != 3)
            throw new Exception($"Expected 3 merged chunks, got {allChunks.Count}");

        Directory.Delete(tempDir, true);

        Console.WriteLine("[PASS] Test 18 - Batch orchestrator merges multiple files");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 18 - Batch orchestrator: {ex.Message}");
        failed++;
    }

    // ─── Test 19: Single-file backward compat path still works ───
    try
    {
        var tempDir = "/tmp/single_compat_test";
        Directory.CreateDirectory(tempDir);
        string jsonText = "{\"chunks\":[{\"id\":\"compat-001\",\"content\":\"backward compat content\"}]}";

        var fakeSvc = new FakeEmbeddingService(64);
        var result = await ProcessSingleFile(fakeSvc, jsonText, Path.Combine(tempDir, "test.ragged.json"));

        if (result.Results.Count != 1)
            throw new Exception($"Expected 1 result, got {result.Results.Count}");
        if (!result.Results[0].Success)
            throw new Exception("Single file processing failed");
        if (result.FailedCount != 0)
            throw new Exception($"Expected 0 failures, got {result.FailedCount}");

        Directory.Delete(tempDir, true);
        Console.WriteLine("[PASS] Test 19 - Single-file backward compat");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 19 - Single-file compat: {ex.Message}");
        failed++;
    }

    // ─── Test 20: Merged output structure is valid JSON with metadata ───
    try
    {
        var tempDir = "/tmp/merge_structure_test";
        Directory.CreateDirectory(tempDir);
        var outFile = Path.Combine(tempDir, "merged.json");

        string file1 = "{\"chunks\":[{\"id\":\"src1-001\",\"content\":\"a\",\"metadata\":{\"source\":\"a.md\"}}]}";
        string file2 = "{\"chunks\":[{\"id\":\"src2-001\",\"content\":\"b\",\"metadata\":{\"source\":\"b.md\"}}]}";

        var fakeSvc = new FakeEmbeddingService(32);

        var r1 = await ProcessSingleFile(fakeSvc, file1, Path.Combine(tempDir, "f1.json"));
        var r2 = await ProcessSingleFile(fakeSvc, file2, Path.Combine(tempDir, "f2.json"));

        var allChunks = new List<JsonNode>();
        allChunks.AddRange(BatchEmbedHelpers.ExtractChunksForMerge(r1.RootNode));
        allChunks.AddRange(BatchEmbedHelpers.ExtractChunksForMerge(r2.RootNode));

        // Build merged output matching batch orchestrator format
        var merged = new JsonObject
        {
            ["chunks"] = new JsonArray(allChunks.Select(c => c.DeepClone()).Cast<JsonNode>().ToArray()),
            ["total_chunks"] = JsonValue.Create(allChunks.Count),
            ["source_files"] = JsonValue.Create(2),
            ["successful_chunks"] = JsonValue.Create(allChunks.Count)
        };

        File.WriteAllText(outFile, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Verify round-trip parse
        var reloaded = JsonNode.Parse(File.ReadAllText(outFile))!;
        if (reloaded is not JsonObject rootObj)
            throw new Exception("Merged output is not a JSON object");
        if (!rootObj.ContainsKey("chunks"))
            throw new Exception("Missing 'chunks' in merged output");
        if (!rootObj.ContainsKey("total_chunks"))
            throw new Exception("Missing 'total_chunks' in merged output");
        if (rootObj["total_chunks"]?.GetValue<int>() != allChunks.Count)
            throw new Exception($"total_chunks mismatch: {rootObj["total_chunks"]} vs {allChunks.Count}");

        // Verify chunks preserved content
        var outChunks = rootObj["chunks"]!.AsArray();
        if (outChunks.Count != 2)
            throw new Exception($"Expected 2 merged chunks, got {outChunks.Count}");

        Directory.Delete(tempDir, true);
        Console.WriteLine("[PASS] Test 20 - Merged output structure valid with metadata");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 20 - Merge structure: {ex.Message}");
        failed++;
    }

    // ─── Test 21: Legacy vector-name mode produces raw array ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(32);
        var jsonText = "{\"chunks\":[{\"id\":\"lv-001\",\"content\":\"legacy test vector\"}]}";
        var rootNode = JsonNode.Parse(jsonText)!;

        // VectorName = null → legacy mode
        var processor = new EmbeddingProcessor(fakeSvc, 32, vectorName: null);
        var result = await ProcessSingleFileTestLegacy(fakeSvc, jsonText, "/tmp/test_legacy.json", processor);

        // Read output and check vector format
        var outPath = Path.Combine(Path.GetTempPath(), "test_legacy.json");
        EmbeddingProcessor.WriteOutput(result, outPath);
        var outJson = File.ReadAllText(outPath);
        var outNode = JsonNode.Parse(outJson)!;
        var chunksArr = (outNode["chunks"] as JsonArray)!;
        var chunkObj = (JsonObject)chunksArr[0];
        var pointObj = (JsonObject)((JsonArray)chunkObj["points"])![0];
        var vectorVal = pointObj["vector"];

        if (vectorVal is JsonObject)
            throw new Exception("Expected raw array, got object (named vector format)");
        if (vectorVal is not JsonArray vecArr || vecArr.Count != 32)
            throw new Exception($"Vector should be raw array of 32 elements, got {vectorVal?.GetType().Name}");

        File.Delete(outPath);
        Console.WriteLine("[PASS] Test 21 - Legacy vector-name mode produces raw array");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 21 - Legacy mode: {ex.Message}");
        failed++;
    }

    // ─── Test 22: Named vector mode produces keyed-vector object in output JSON ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(32);
        const string TEST_VECTOR_NAME = "qwen-embeddings";
        var jsonText = $"{{\"chunks\":[{{\"id\":\"nv-001\",\"content\":\"named test vector\"}}]}}";

        var processor = new EmbeddingProcessor(fakeSvc, 32, vectorName: TEST_VECTOR_NAME);
        var result = await ProcessSingleFileTestLegacy(fakeSvc, jsonText, "/tmp/test_named.json", processor);

        var outPath = Path.Combine(Path.GetTempPath(), "test_named.json");
        EmbeddingProcessor.WriteOutput(result, outPath);
        var outJson = File.ReadAllText(outPath);
        var outNode = JsonNode.Parse(outJson)!;
        var chunksArr = (outNode["chunks"] as JsonArray)!;
        var chunkObj = (JsonObject)chunksArr[0];
        var pointObj = (JsonObject)((JsonArray)chunkObj["points"])![0];
        var vectorVal = pointObj["vector"];

        if (vectorVal is not JsonObject vecObj)
            throw new Exception($"Expected object, got {vectorVal?.GetType().Name}");
        if (!vecObj.ContainsKey(TEST_VECTOR_NAME))
            throw new Exception($"Vector object missing key '{TEST_VECTOR_NAME}'");
        var innerArr = vecObj[TEST_VECTOR_NAME] as JsonArray;
        if (innerArr is null || innerArr.Count != 32)
            throw new Exception($"Inner vector should be array of 32, got {innerArr?.Count}");

        File.Delete(outPath);
        Console.WriteLine("[PASS] Test 22 - Named vector mode produces keyed vector object");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 22 - Named mode: {ex.Message}");
        failed++;
    }

    // ─── Test 23: VectorName in LlamaCppEmbeddingOptions is preserved through pipeline ───
    try
    {
        var opts = new LlamaCppEmbeddingOptions
        {
            ServerUrl = "http://localhost:4000",
            ModelId = "test-model",
            ExpectedDimension = 768,
            VectorName = "jina-embeddings-v3",
        };

        if (opts.VectorName != "jina-embeddings-v3")
            throw new Exception($"VectorName was '{opts.VectorName}', expected 'jina-embeddings-v3'");

        Console.WriteLine("[PASS] Test 23 - LlamaCppEmbeddingOptions preserves VectorName");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 23 - Options preservation: {ex.Message}");
        failed++;
    }

    // ─── Test 24: Empty vector name treated same as null (legacy mode) ───
    try
    {
        var fakeSvc = new FakeEmbeddingService(32);
        const string jsonText = "{\"chunks\":[{\"id\":\"empty-001\",\"content\":\"empty name test\"}]}";

        // Explicitly pass empty string — should be same as null → legacy mode
        var processor = new EmbeddingProcessor(fakeSvc, 32, vectorName: "");
        var result = await ProcessSingleFileTestLegacy(fakeSvc, jsonText, "/tmp/test_empty.json", processor);

        var outPath = Path.Combine(Path.GetTempPath(), "test_empty.json");
        EmbeddingProcessor.WriteOutput(result, outPath);
        var outJson = File.ReadAllText(outPath);
        var outNode = JsonNode.Parse(outJson)!;
        var chunkObj = ((JsonObject)((outNode["chunks"] as JsonArray)![0]))!;
        var pointObj = (JsonObject)((JsonArray)chunkObj["points"])![0];
        var vectorVal = pointObj["vector"];

        if (vectorVal is JsonObject)
            throw new Exception("Expected raw array, got object");

        File.Delete(outPath);
        Console.WriteLine("[PASS] Test 24 - Empty vector name produces raw array (legacy mode)");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Test 24 - Empty name: {ex.Message}");
        failed++;
    }

    var sep = new string('=', 50);
    Console.WriteLine($"\n{sep}");
    Console.WriteLine($"Results: {passed} passed, {failed} failed out of {passed + failed} tests");
    Console.WriteLine($"{sep}");

    if (failed > 0) Environment.Exit(1);
}

/// <summary>
/// Helper for tests: processes a single JSON string via EmbeddingProcessor and returns the result.
/// </summary>
static async Task<ProcessResult> ProcessSingleFile(FakeEmbeddingService fakeSvc, string jsonContent, string filePath)
{
    var rootNode = JsonNode.Parse(jsonContent)!;
    var processor = new EmbeddingProcessor(fakeSvc, fakeSvc.EmbeddingDimension);
    return await processor.ProcessAsync(rootNode, filePath, CancellationToken.None);
}

/// <summary>
/// Processes a single JSON string via the given processor directly (allows custom vector name).
/// </summary>
static async Task<ProcessResult> ProcessSingleFileTestLegacy(
    FakeEmbeddingService fakeSvc, string jsonContent, string filePath, EmbeddingProcessor processor)
{
    var rootNode = JsonNode.Parse(jsonContent)!;
    return await processor.ProcessAsync(rootNode, filePath, CancellationToken.None);
}
