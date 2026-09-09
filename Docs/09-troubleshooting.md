# Troubleshooting

## Common Pitfalls

These are the known issues, ordered by frequency:

### 1. Dimension Mismatch on First Chunk

**Symptom:** `InvalidOperationException: Embedding dimension mismatch. Expected: X, Actual: Y.`

**Cause:** The `EMBEDDING_DIMENSION` environment variable is set to a value that doesn't match the actual embedding model's output dimension. Only validated on the **first** successful call (chunk 0).

**Fix:** Either unset `EMBEDDING_DIMENSION` (let it auto-detect) or set it to the correct value:
```bash
# Auto-detect (recommended for development)
unset EMBEDDING_DIMENSION

# Or verify with your model's documentation
export EMBEDDING_DIMENSION=4096  # example for Qwen3-Embedding
```

### 2. Port Confusion: 4000 vs 6333

**Symptom:** Connection refused or wrong error messages.

**Cause:** llama.cpp runs on port **4000**; Qdrant runs on port **6333**. These are completely different services.

| Service | Default Port | Purpose |
|---------|-------------|---------|
| llama.cpp | 4000 | Embedding endpoint `/v1/embeddings` — called by C# app |
| Qdrant | 6333 | Vector database — used by `setup_qdrant.py` |

### 3. Test File Not Found

**Symptom:** Tests print `[FAIL] Test 1 - JSON loading: Could not read file './Chunked Data.json'`

**Cause:** The test project looks for `./Chunked Data.json` in its working directory (`Tests/`), but the file lives in `Reference/`.

**Fix:** Copy it first:
```bash
cp "../Reference/Chunked Data.json" "Tests/"
cd "Tests" && dotnet run
```

### 4. output.json vs .embedded.json Confusion

**Symptom:** `setup_qdrant.py` can't find `output.json`.

**Cause:** The C# app auto-appends `.embedded.json` to the input filename, but `setup_qdrant.py` reads a hardcoded path: `/home/clarorecto/.../output.json`. These are different files.

**Fix:** Either:
- Pass explicit output path: `dotnet run -- embed "input.json" "./output.json"`
- Or supply the same path to the upsert command: `python3 setup_qdrant.py upsert -j ./your_path.json`

### 5. HTTP Timeout on Large Chunks (RAG Pipeline)

**Symptom:** `02 Semantic timed out after Nm.` or similar stage timeout error.

**Cause:** The per-stage timeout (`_llamaTimeout`) limits how long each LLM call can take before being cancelled. For large markdown files with many segments or long reasoning output, the default may not be enough.

**Fix:** Use the `--timeout` flag to set a longer per-stage limit (in minutes):
```bash
# Increase to 15 minutes per stage
dotnet run -- rag file.md --timeout 15

# Increase to 30 minutes per stage for very large files
dotnet run -- rag file.md --timeout 30
```

The `RagService.HttpClient` has its own separate 15-minute HTTP timeout (configured in `RagService.cs:21`), so any per-stage timeout you set should stay at or below 15 minutes to avoid double-timeouts.

### 6. Timeout on Embed Command

**Symptom:** `TimeoutException` or HTTP timeout during embedding.

**Cause:** The default 5-minute HTTP timeout per chunk is exceeded, typically for very large chunks sent to llama.cpp.

**Fix:** If using a custom server, verify the server's performance. For embedded JSON processing, consider pre-chunking into smaller segments before running the embed command.

### 7. Invalid JSON Input

**Symptom:** `Error: Invalid JSON in input file: ...`

**Cause:** Malformed or corrupted input file.

**Fix:** Validate the JSON with a tool before running:
```bash
python3 -c "import json; json.load(open('input.json'))" && echo "Valid JSON"
```

### 8. No Chunks Found

**Symptom:** `InvalidOperationException: No chunks found in input JSON. Expected a 'chunks' array at the root level.`

**Cause:** The input JSON doesn't have a top-level `"chunks"` key, or the value isn't an array of objects.

**Fix:** Verify the input structure matches what `ExtractChunks()` expects:
```json
{
  "chunks": [
    { "id": "...", "content": "..." }
  ]
}
```
If chunks are nested under a different key, the fallback logic scans root values for any array of objects.

### 9. Rag Pipeline OOM — Intermediate Files Not Found

**Symptom:** After an OOM during Chunk stage, re-running `rag` says "No intermediate results found — restarting from Segment."

**Cause:** The AgenticChunkingProcessor saves segment and semantic-grouping intermediates to disk for resumption. If those files were deleted or the output dir moved, the pipeline restarts from Stage 1.

**Fix:** Ensure your intermediate output directory exists and contains `segment_output.json` and `semantic_groups.json`. They're created after Stage 1 (Segment) and Stage 2 (Semantic Grouping). Check:
```bash
ls ./rag_output/segment_output.json ./rag_output/semantic_groups.json
```

### 10. Rag Service Returns Empty Object `{}` or Null

**Symptom:** `NullReferenceException` during rag processing when parsing LLM output.

**Cause:** The LLM response contains no balanced brace/bracket blocks, or all extracted blocks are arrays (not objects). `ExtractJsonFromText()` prefers objects over arrays of equal size.

**Fix:** Check that your prompt templates include clear JSON structure instructions. Verify the raw LLM response:
```csharp
// Debug by printing raw response before extraction in RagService.SendPromptAsync()
```
If the model returns valid content but not structured JSON, adjust the prompt to enforce a specific JSON schema.

### 11. Rag Reasoning Tokens Format Mismatch

**Symptom:** llama.cpp rejects the request with a format error about `reasoning_tokens`.

**Cause:** Different models use different keys for reasoning tokens: Qwen uses `"reasoning_tokens"`, while some other models use `"thinking"` or `"reasoning"`. The app hardcodes `reasoning_tokens: -1` in RagService.cs line 87.

**Fix:** If using a non-Qwen model, change the key in `RagService.SendPromptAsync()`:
```csharp
// For Qwen models (current):
model["reasoning_tokens"] = -1;
// For other models:
model["thinking"] = "enabled";  // or model["reasoning"] = true;
```

### 12. Pipeline Logger Log File Not Appearing

**Symptom:** After running `rag` or `embed`, the expected `<filename>_pipeline.log.txt` does not appear in the output directory.

**Cause:** The logger only flushes to disk when `Dispose()` is called (end of processing), when an error occurs, or when the 40k-char truncation threshold is hit mid-stream. If the process crashes before disposal, the log may be incomplete.

**Fix:** Check for partial log content in the output directory. For live debugging, examine the stderr console output which mirrors the logger's stage transitions.

### 13. Stage 3 DATA LOSS CHECK Failures

**Symptom:** Processing completes but `stage3_final_chunks` differs from expected, or chunks appear truncated compared to source.

**Cause:** If the LLM rewrites content instead of copying it verbatim, the DATA LOSS CHECK compares generated chunks against source markdown headings. Mismatches may indicate content loss.

**Fix:** Check the `<filename>_pipeline.log.txt` for DATA LOSS CHECK results. Adjust prompt template (`03 Chunking.md`) to enforce stricter "copy verbatim" instructions.

### 14. Embed Command Missing `retrieval_content` in Qdrant Payload

**Symptom:** After upsert, Qdrant payloads lack the `retrieval_content` field even though the `.ragged.json` has it.

**Cause:** The embed processor copies all properties from the chunk into the Qdrant point payload except `"points"`. If a custom script or older embedded JSON format is used, `retrieval_content` may be stripped during transformation.

**Fix:** Verify that `EmbeddingProcessor.BuildQdrantPoint()` preserves all original chunk properties. The processor does a `DeepClone()` on the original chunk's properties — `retrieval_content` should pass through automatically if present in the input JSON.

## Error Handling Reference

| Error | Where Caught | Message Format |
|-------|-------------|----------------|
| Missing input arg | Program.cs L10-23 | `Usage: dotnet run -- <command> [options]` |
| File not found | Program.cs L27-31 | `Error: Input file not found: {path}` |
| Can't read file | Program.cs L58-66 | `Error: Could not read input file: {message}` |
| Invalid JSON | Program.cs L69-77 | `Error: Invalid JSON in input file: {message}` |
| No chunks found | EmbeddingProcessor.L49-50 | `No chunks found in input JSON. Expected a 'chunks' array...` |
| Empty chunk content | EmbeddingProcessor.L73-78 | `Warning: Chunk '{id}' has empty or null content — skipping.` |
| HTTP error | EmbeddingProcessor.L111-113 | `Error: Failed to generate embedding for chunk '{id}': HTTP {code} ({status})` |
| Dimension mismatch | EmbeddingProcessor.L87-90 | `Embedding dimension mismatch. Expected: {expected}, Actual: {actual}. First chunk: {id}` |
| Write failure | Program.cs L126-130 | `Error: Could not write output file: {message}` |
| Rag: input neither files nor dir | Program.cs (rag branch) | `Error: No input specified. Provide markdown files or --input-dir.` |
| Rag: server URL required | AgenticChunkingProcessor | `Error: --llama-url required (LLAMA_CPP_URL is not set).` |
| Rag: segment failed | AgenticChunkingProcessor | `Error: Failed to segment file '{file}': {error}` |
| Rag: semantic grouping failed | AgenticChunkingProcessor | `Error: Failed to group segments for '{file}': {error}` |
|| Rag: chunk generation failed | AgenticChunkingProcessor | `Error: Failed to generate chunks for '{file}' stage {stage}: {error}` |
|| Stage 3 DATA LOSS CHECK | AgenticChunkingProcessor | `Stage 3 data loss check: N of M chunks verified` (logged) |
|| Pipeline logger write error | Utils/PipelineLogger | `Error writing pipeline log to {path}: {message}` |

## Troubleshooting Strategy

The project's original spec defines a preferred approach (see Prompt.md §18-21):

```
Encounter error
    → Inspect error message
    → Inspect relevant local code/configuration
    → Determine likely cause
    → Can it be fixed locally?
        ├─ YES → Fix → Test
        └─ NO
            → Search for authoritative documentation
            → Apply evidence-based fix
            → Test again
```

**Key rule:** If the same error occurs twice without a meaningful change in diagnosis, search external docs rather than blind-retrying.

## Related Documents

- [Getting Started](02-getting-started.md) — quick fixes table (subset of this document)
- [Project Overview](01-project-overview.md) — architecture context for understanding where errors originate
- [Core Components](03-core-components.md) — which class/line throws each error
- [RAG Pipeline Docs](04-rag-pipeline.md) — RagService and AgenticChunkingProcessor troubleshooting
