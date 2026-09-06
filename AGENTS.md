# Embedding Console — Agent Instructions

A .NET 10 C# console application that reads a JSON file containing text chunks, generates embeddings via a llama.cpp server, and writes the enriched output back. A companion Python script (`setup_qdrant.py`) creates a Qdrant collection and upserts all computed embeddings.

## Environment

- **Runtime:** .NET 10 (`dotnet --version` → 10.0.111 on this machine). NuGet packages cached at `~/.nuget/packages/`.
- **Single dependency:** `System.Net.Http.Json` v8.0.1 (transitive for HTTP calls to llama.cpp).
- **Python tooling:** `qdrant-client` for `setup_qdrant.py`; requires a running Qdrant instance at `http://localhost:6333`.

## Build & Test

```bash
# Build the main project
dotnet build "Embedding Console.csproj"

# Run tests (inline tests, no framework)
cd "Tests" && dotnet run

# Clean build artifacts
dotnet clean; rm -rf bin obj Tests/bin Tests/obj
```

### Pre-Commit Analysis
Before committing any changes, always:
1. **Analyze changes first**: Run `git status` and `git diff` to understand what files have been modified
2. **Check for conflicts**: Ensure no merge conflicts or uncommitted changes exist
3. **Verify build**: Run the build and test commands above to confirm everything works
4. **Update documentation**: If you've added/modified functionality, update this AGENTS.md file accordingly

The test project (`Tests/Tests.csproj`) references the main project via `<ProjectReference>`. It runs 15 inline assertions — not a test framework, just `try/catch` with PASS/FAIL output. Exit code 1 means failures.

## Running the App

The app uses a command dispatch system. Currently one command is available: `embed`.

```bash
# General usage
dotnet run -- --help

# Embed command
dotnet run -- embed <input.json> [output.json]
dotnet run -- embed <input.json> -o <output.json>
dotnet run -- embed --help
```

The `embed` subcommand accepts:
- `<input.json>` — Required. Path to the JSON file with chunks to embed.
- `[output.json]` — Optional. Alternative output path (auto-generated to `<input>.embedded.json` if omitted).
- `-o <path>` / `--output <path>` — Alternative flag for explicit output path.

Required env vars:
| Var | Default | Purpose |
|-----|---------|---------|
| `LLAMA_CPP_URL` | `http://localhost:4000` | llama.cpp embedding server endpoint |
| `LLAMA_CPP_MODEL` | _(none)_ | model ID string, shown in output log |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | expected vector dimension; set to avoid first-chunk mismatch errors |

### Python Script (`setup_qdrant.py`)

The script now uses two separate subcommands:

```bash
# Create a collection — queries llama.cpp for the embedding model dimension, then creates the collection
python3 setup_qdrant.py create --llama-url http://localhost:4000       # auto-detect dimension
python3 setup_qdrant.py create --dimension 4096                        # manual override

# Upsert points from a JSON file (output of the C# pipeline)
python3 setup_qdrant.py upsert -j ./output.json
```

Global options:
| Flag | Default | Purpose |
|------|---------|---------|
| `--collection`, `-c` | `"Coding Knowledge"` | Target Qdrant collection name |
| `--qdrant-url` | `http://localhost:6333` | Qdrant server URL |

`create` subcommand flags:
| Flag | Default | Purpose |
|------|---------|---------|
| `--llama-url`, `-l` | _(none)_ | llama.cpp server URL; queried via `/v1/models` to auto-detect the model's embedding dimension (`n_embd`) |
| `--dimension`, `-d` | `0` (auto-detect) | Force vector dimension; required when `--llama-url` is omitted |

`upsert` subcommand flags:
| Flag | Required | Purpose |
|------|----------|---------|
| `--json-path`, `-j` | yes | Path to the JSON file containing embedded chunks (e.g. `./output.json` or `./output.embedded.json`) |

The collection is configured with float32 vectors, COSINE distance, single-segment indexing, HNSW (m=6, ef_construct=128), and TurboQuant 4-bit search index with rescoring against original float32. See the inline docstrings for full rationale.

## Project Structure

```
Embedding Console.csproj   ← main project (net10.0, ImplicitUsings+Nullable)
Program.cs                 ← CLI entry: arg parsing → env config → processor pipeline
Processors/EmbeddingProcessor.cs  ← core ETL: extract chunks, call embedding service, build Qdrant points
Services/IEmbeddingService.cs   ← interface (GenerateEmbeddingAsync + EmbeddingDimension)
Services/LlamaCppEmbeddingService.cs ← real HTTP impl against llama.cpp
Services/FakeEmbeddingService.cs    ← deterministic hash-based vectors for tests
Models/QdrantPoint.cs            ← record(Id, Vector, Payload)
Tests/                       ← inline test harness (dotnet run)
  Program.cs                 ← 15 PASS/FAIL assertions
setup_qdrant.py              ← Qdrant collection + batch upsert (python3)
Reference/                   ← project prompts and chunked data samples
```

## Conventions

- **Namespaces:** mirror directory structure (`Embedding_Console.Processors`, `Embedding_Console.Services`, etc.) — underscores, not PascalCase.
- **Type system:** `<Nullable>enable</Nullable>` + `<ImplicitUsings>enable</ImplicitUsings>`. All public APIs have XML doc comments. Records for immutable data (QdrantPoint, ProcessResult, ChunkResult).
- **Async style:** `async/await` everywhere; HttpClient scoped and disposed at end of Program.cs.
- **JSON handling:** `System.Text.Json.Nodes` (`JsonNode`, `JsonObject`, `JsonArray`) — NOT the binary serializer. Output preserves original property names.

## Pitfalls

1. **Tests use relative paths.** Tests assume `./Chunked Data.json` exists in the working directory. Run from the `Tests/` dir or copy the file there first.
2. **`LLAMA_CPP_URL` defaults to `:4000`, not `:6333`.** Port 4000 is llama.cpp, port 6333 is Qdrant — don't confuse them.
3. **`output.json` vs `.embedded.json`.** The Python setup script reads hardcoded `/home/clarorecto/.../output.json`. The C# app writes `<input>.embedded.json`. These are different files.
4. **Dotnet project name has a space.** `"Embedding Console.csproj"` — always quote paths containing spaces in shell commands.
5. **Dimension mismatch on first chunk.** If `EMBEDDING_DIMENSION` is set, the first embedding MUST match that dimension or `ProcessAsync` throws. Verify your model before running.

## Git Commits

This repo uses a structured commit convention observed in history: `<TYPE>-<ID> : <title>` (e.g. `FEAT-3K79 : Add Qdrant collection setup script`). Generate identifiers by checking recent `git log` to avoid collisions — do not invent or reuse IDs from other branches.

**Pre-Commit Analysis Checklist:**
- [ ] Run `git status` to see all modified files
- [ ] Run `git diff` to review changes before staging
- [ ] Verify all tests pass with `cd Tests && dotnet run`
- [ ] Update AGENTS.md if documentation was modified
- [ ] Use descriptive commit messages that explain WHAT and WHY
- [ ] Group related changes logically, not by file location
- [ ] Stage specific files with `git add <path>` (never `git add .`)
- [ ] Verify commit shows clean status with `git status --short`

**Commit Format:**
```text
<CHANGE_TYPE>-<UNIQUE_SUFFIX> : <commit_title>

## Summary

- <high-level change>
- <high-level change>

## Details

- <implementation detail>
- <implementation detail>
```

**Rules:**
- Never stage, modify, discard, or reset unrelated working-tree changes
- Never push — commits stay local unless explicitly requested
- Inspect all changes before committing — group files by logical purpose, not by when they were touched together

**Format:**
```text
<CHANGE_TYPE>-<UNIQUE_SUFFIX> : <commit_title>

## Summary

- <high-level change>
- <high-level change>

## Details

- <implementation detail>
- <implementation detail>
```

Change types: `FEAT`, `BUGFIX`, `CHORE`, `DOC`, `TEST`, `CONFIG`. Suffixes are short alphanumeric codes.

**Rules:**
- Inspect all changes (`git status`, `git diff`) before committing — do not blindly group by directory.
- Group files by logical purpose, not by when they were touched together. Independent features → separate commits.
- Stage explicitly per group: `git add path/to/file.cs` — never use `git add .` or `git add -A`.
- Verify after each commit: `git log -1 --oneline`, then `git status --short` to confirm remaining changes are untouched.
- Never stage, modify, discard, or reset unrelated working-tree changes.
- Never push — commits stay local unless the agent explicitly pushes.
