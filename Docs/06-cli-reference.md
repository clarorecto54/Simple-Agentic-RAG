# CLI Reference

## Command Dispatch

The app uses a command dispatch system with two commands: `embed` and `rag`. List available commands:

```bash
dotnet run -- --help
```

### Available Commands

```bash
# Embed command — JSON embedding pipeline
dotnet run -- embed <input.json> [output.json]
dotnet run -- embed --help

# RAG command — agentic markdown chunking pipeline
dotnet run -- rag <file1.md> [file2.md ...] [options]
dotnet run -- rag --help
```

All subcommands and the top-level help support `--help` / `-h`. Help text is centralised in `HelpText.cs` for easy maintenance.

### RAG Subcommand Options

| Argument / Flag | Required | Description |
|-----------------|----------|-------------|
| `[file1.md ...]` | No | One or more markdown files to process. If omitted, use `--input-dir`. |
| `--input-dir, -d DIR` | No | Directory to scan recursively for `.md`/`.markdown` files |
| `--output-dir, -o DIR` | No | Output directory for `.ragged.json` files (default: `./rag_output`) |
| `--llama-url, -l URL` | No | llama.cpp server URL (default: `$LLAMA_CPP_URL` or `http://localhost:4000`) |
| `--prompt-dir, -p DIR` | No | Directory containing prompt templates (default: `./Reference`) |

When `--input-dir` is used, the app finds all `.md` and `.markdown` files recursively and processes them in sorted order. Per-file results are saved as `<filename>.ragged.json`. Each file goes through 3 stages: Segment → Semantic → Chunk, with intermediate results saved so OOM errors preserve progress.

### Subcommand Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `<input.json>` | Yes | Path to the JSON file containing chunks (must exist) |
| `[output.json]` | No | Optional path for the enriched output file |

### Alternative Output Flag

You can also use `--output` / `-o` instead of positional second argument:

```bash
dotnet run -- embed <input.json> -o <output.json>
dotnet run -- embed <input.json> --output <output.json>
```

## Output Path Behavior

If the output path is **not provided** (and `-o` is not used), the app auto-generates it by appending `.embedded.json` to the input filename:

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

### Console Output on Failure

Example — missing input file:
```
Error: Input file not found: ./nonexistent.json
```

Example — dimension mismatch (embed):
```
Error: Embedding dimension mismatch. Expected: 1024, Actual: 4096. First chunk: chunk-001
```

Example — HTTP error:
```
Error: Failed to generate embedding for chunk 'chunk-001': HTTP 503 (ServiceUnavailable)
```

### Console Output — RAG Pipeline

Rag output shows per-stage progress and summary:
```
=== RAG Processing Pipeline ===
  Input files:    Reference/Project Structure.md, Reference/Reference Data.json
  Output dir:     ./rag_output
  Server URL:     http://localhost:4000
  Model ID:       qwen2.5-14b-instruct

Processing file: Reference/Project Structure.md (2838 chars)
  Segment:   1 segments in 0.8s
  Semantic:  Found 4 semantic groups in 6.1s
  Chunk:     12 chunks written to ./rag_output/Project Structure.ragged.json

Results
  Files processed:       2
  Total input bytes:     14,672
  Successful:            2
  Failed:                0
  Time elapsed:          9.4s
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
