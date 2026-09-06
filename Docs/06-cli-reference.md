# CLI Reference

## Command Syntax

```bash
dotnet run -- <input.json> [output.json]
```

## Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `<input.json>` | Yes | Path to the JSON file containing chunks (must exist) |
| `[output.json]` | No | Optional path for the enriched output file |

## Output Path Behavior

If the output path is **not provided**, the app auto-generates it by appending `.embedded.json` to the input filename:

| Input Path | Auto-Generated Output |
|------------|----------------------|
| `./data/chunks.json` | `./data/chunks.embedded.json` |
| `Reference/Chunked Data.json` | `Reference/Chunked Data.embedded.json` |
| `/tmp/input.json` | `/tmp/input.embedded.json` |

This is computed in Program.cs:
```csharp
var dir = Path.GetDirectoryName(inputPath) ?? ".";
var nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
outputPath = Path.Combine(dir, $"{nameWithoutExt}.embedded.json");
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success — output file written |
| 1 | Error — printed to stderr with descriptive message |

## Console Output on Success

```
=== JSON Embedding Pipeline ===
  Input file:   ./Reference/Chunked Data.json
  Output file:  ./output/embedded.json
  Server URL:   http://localhost:4000
  Model ID:     (auto)

Generating embeddings...
Processed: 33/33

Results
  Input:   ./Reference/Chunked Data.json
  Dimension: 4096
  Chunks processed: 33
  Successful:         33
  Failed:             0
  Embedding dimension: 4096
  Time elapsed:        12.4s

  Output:       ./output/embedded.json
  Completed successfully.
```

## Console Output on Failure

Example — missing input file:
```
Error: Input file not found: ./nonexistent.json
```

Example — dimension mismatch:
```
Error: Embedding dimension mismatch. Expected: 1024, Actual: 4096. First chunk: chunk-001
```

Example — HTTP error:
```
Error: Failed to generate embedding for chunk 'chunk-001': HTTP 503 (ServiceUnavailable)
```

## Environment Variable Reference

| Variable | Default | Purpose |
|----------|---------|---------|
| `LLAMA_CPP_URL` | `http://localhost:4000` | llama.cpp embedding server endpoint |
| `LLAMA_CPP_MODEL` | _(none)_ | Model ID, shown in the "Model ID" line of startup output |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Expected vector dimension; validated against first successful response |

## Progress Reporting

Progress is printed at:
- Start — `Input`, number of chunks found
- During processing — every 10th chunk: `Processed: N/M`
- On completion — summary with counts, dimension, elapsed time

Chunk-level errors are printed to stderr as they occur:
```
Error: Failed to generate embedding for chunk 'chunk-042': HTTP 504 (GatewayTimeout)
```

## Related Documents

- [Getting Started](02-getting-started.md) — environment setup and first run examples
- [Project Overview](01-project-overview.md) — full architecture context
- [Core Components](03-core-components.md) — how Program.cs wires services to the processor
