# Getting Started

## Prerequisites

- **.NET 10 SDK** (v10.0.111 on this machine)
- **Python 3.x** with `qdrant-client` package (for `setup_qdrant.py`)
- **Running llama.cpp server** at a configurable URL (default: `http://localhost:4000`)
- **Running Qdrant instance** at `http://localhost:6333` (only for `setup_qdrant.py`)

## Build

```bash
dotnet build "Embedding Console.csproj"
```

## Run

```bash
# Basic usage
dotnet run -- embed <input.json>

# With explicit output path
dotnet run -- embed <input.json> <output.json>
dotnet run -- embed <input.json> -o <output.json>
```

If no output path is given, the app auto-generates `<input>.embedded.json` in the same directory.

## Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `LLAMA_CPP_URL` | `http://localhost:4000` | llama.cpp embedding server endpoint |
| `LLAMA_CPP_MODEL` | _(none)_ | Model ID string, shown in output log |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | Expected vector dimension; set to 0 for auto-detection on first response |

### Setting Environment Variables

```bash
# Linux/macOS
export LLAMA_CPP_URL=http://localhost:4000
export EMBEDDING_DIMENSION=4096

# Windows (cmd)
set LLAMA_CPP_URL=http://localhost:4000

# Windows (PowerShell)
$env:LLAMA_CPP_URL="http://localhost:4000"
```

### Example Commands

```bash
# Auto-detect dimension, use default server
dotnet run -- embed "./Reference/Chunked Data.json"

# Fixed 4096 dimensions on custom server with named output
export LLAMA_CPP_URL=http://my-server:4000
export EMBEDDING_DIMENSION=4096
dotnet run -- embed "./Reference/Chunked Data.json" "./output/embedded.json"
```

## Run Tests

```bash
cd "Tests" && dotnet run
```

The test project runs **20 inline tests** (no test framework). It uses `FakeEmbeddingService` so no network calls are made. Exit code 1 means failures.

**Note:** Tests assume `./Chunked Data.json` exists in the working directory (`Tests/`). Copy it there first if needed:
```bash
cp "../Reference/Chunked Data.json" "Tests/"
cd "Tests" && dotnet run
```

## Setup Qdrant

The script uses two subcommands — run them in sequence:

```bash
# Step 1: Create the collection (queries llama.cpp for model dimension, or use --dimension)
python3 setup_qdrant.py create --llama-url http://localhost:4000
# Or with manual dimension:
python3 setup_qdrant.py create --dimension 4096

# Step 2: Upsert points from the JSON output file
python3 setup_qdrant.py upsert -j ./output.json
```

### Python Dependencies

```bash
pip install qdrant-client
```

> **Port reminder:** llama.cpp defaults to port 4000, Qdrant defaults to port 6333 — do not confuse them.

## Troubleshooting Quick Reference

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| First chunk fails with dimension mismatch | `EMBEDDING_DIMENSION` set but doesn't match model | Remove var or set to correct value |
| HTTP timeout | llama.cpp slow / large chunks | Check server health; increase timeout in code if needed |
| `No 'chunks' property found` | Input JSON missing `chunks` array | Verify input format matches expected schema |
| Tests fail with file not found | Missing test data in working directory | Copy `Chunked Data.json` to `Tests/` directory |

## Related Documents

- [Project Overview](01-project-overview.md) — full architecture and design decisions
- [CLI Reference](06-cli-reference.md) — detailed argument parsing and output path logic
- [Troubleshooting](09-troubleshooting.md) — complete pitfall list with root-cause analysis
