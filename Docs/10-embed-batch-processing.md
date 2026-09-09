# Embed Batch Processing

When multiple input files need embedding, the `embed` command can accept them all at once. This produces a single merged output with per-file error reports for any failures.

## Invocation Patterns

### Multiple Input Files

```bash
# Process several files into one merged output
dotnet run -- embed file1.ragged.json file2.ragged.json -o ./output/

# With explicit env vars
LLAMA_CPP_URL=http://localhost:4000 dotnet run -- embed a.json b.json c.json -o ./batch_output/
```

### Directory Scan (All JSON Files)

```bash
# Automatically scan and process every .json file in a directory tree
dotnet run -- embed --input-dir ./rag_output -o ./embeddings/
```

The `--input-dir` flag scans recursively for `*.json` files, sorted alphabetically by full path. This is the fastest way to embed an entire set of `.ragged.json` outputs from a prior RAG run.

## Output Structure

### Merged Output (`embedded.json`)

When multiple files are processed, the output is a single JSON file with this structure:

```json
{
  "chunks": [ /* all chunks from all input files, in order of processing */ ],
  "total_chunks": 156,
  "source_files": 3,
  "successful_chunks": 156
}
```

- **`chunks`** — A flat array containing every successfully embedded chunk. Each chunk retains its original properties (`id`, `content`, `retrieval_content`, `metadata`, etc.) plus the new `points` array with the embedding vector and Qdrant payload.
- **`total_chunks`** — Sum of all chunks found across all input files (including from failed files).
- **`source_files`** — Number of input files processed.
- **`successful_chunks`** — Count of chunks that were successfully embedded.

### Error Reports (Per Failed File)

If any file fails (either because one or more of its chunks had embedding errors, or the file could not be read/parsed), an error report is written alongside the merged output:

```
<output_dir>/<basename_without_extension>.embed_errors.json
```

Example for `./input/myfile.ragged.json` → `./output/myfile.embed_errors.json`:

```json
{
  "source_file": "./input/myfile.ragged.json",
  "error_chunks": [
    {
      "id": "build-options-build-target-012",
      "content_key": "content",
      "error": "HTTP 504 (GatewayTimeout)"
    }
  ],
  "success_count": 32,
  "failure_count": 1
}
```

If an entire file fails to parse or read:

```json
{
  "source_file": "./input/broken.json",
  "error": "Unexpected end of input",
  "success_count": 0,
  "failure_count": -1
}
```

## Processing Model

The batch orchestrator processes files **sequentially**:

1. Load JSON from the first file
2. Run `EmbeddingProcessor.ProcessAsync()` on it
3. Extract successful chunks via `BatchEmbedHelpers.ExtractChunksForMerge()` and add them to the shared list
4. If any chunks failed, write an error report for that file
5. Repeat for each subsequent file
6. After all files are processed, write the merged output

This means **a single embedding service instance** is reused across all files — important for performance since HTTP connections are kept alive on the shared `HttpClient`.

## Backward Compatibility

Single-file mode is unchanged from before this feature:

```bash
# Exactly one input file — legacy behavior preserved
dotnet run -- embed ./Reference/Chunked Data.json ./output/chunks.embedded.json
```

The single-file path does not create error reports; it uses the existing output format with just the original chunks plus added `points`.

## Internal: ExtractChunksForMerge

`BatchEmbedHelpers.ExtractChunksForMerge()` is the key helper that extracts chunk nodes from a processed JSON root. It:

1. Tries the `chunks` key at the root of the object (the common case for `.ragged.json` files)
2. Falls back to scanning any array value on the root object where the first element is an object

This ensures compatibility whether the input has `"chunks"` as a top-level key or uses another array key. The method is marked `public` with `[InternalsVisibleTo]` so the test project can exercise it directly.

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | All files processed without errors; merged output written |
| 1 | One or more chunks failed embedding (error reports still written for each failed file) or a full-file error occurred |

Error conditions are printed to stderr alongside the per-file error report paths.

## Performance Notes

- Batch processing reuses a single `HttpClient` with a 5-minute timeout across all files — this is significantly faster than launching separate processes.
- The orchestrator creates one `EmbeddingProcessor` per file (each holds its own references but shares the same embedding service and HTTP client).
- Memory usage scales linearly with total chunks — all chunk nodes are cloned into memory before the merged output is written. For very large batches (thousands of files), consider processing in directory-sized batches.

## Related Documents

- [CLI Reference](06-cli-reference.md) — full command-line argument list and examples
- [Getting Started](02-getting-started.md) — basic embed usage
- [Core Components](03-core-components.md) — EmbeddingProcessor internals
