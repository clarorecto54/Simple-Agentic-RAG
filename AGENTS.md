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

The test project (`Tests/Tests.csproj`) references the main project via `<ProjectReference>`. It runs 16 inline assertions — not a test framework, just `try/catch` with PASS/FAIL output. Exit code 1 means failures.

## Running the App

The app uses a command dispatch system. Available commands: `embed` and `rag`.

```bash
# General usage
dotnet run -- --help

# Embed command
dotnet run -- embed <input.json> [output.json]
dotnet run -- embed <input.json> -o <output.json>
dotnet run -- embed --help

# RAG command
dotnet run -- rag <file1.md> [file2.md ...] [options]
dotnet run -- rag --help
```

The `embed` subcommand accepts:
- `<input.json>` — Required. Path to the JSON file with chunks to embed (typically a `.ragged.json` from the RAG pipeline).
- `[output.json]` — Optional. Alternative output path (auto-generated to `<input>.embedded.json` if omitted, e.g. `file.ragged.embedded.json`).
- `-o <path>` / `--output <path>` — Alternative flag for explicit output path.

The `rag` subcommand accepts:
- `[file1.md ...]` — Optional. One or more markdown files to process.
- `--input-dir, -d DIR` — Directory to scan recursively for `.md`/`.markdown` files.
- `--output-dir, -o DIR` — Output directory for `.ragged.json` files (default: `./rag_output`).
- `--llama-url, -l URL` — llama.cpp server URL (default: `$LLAMA_CPP_URL` or `http://localhost:4000`).
- `--prompt-dir, -p DIR` — Directory containing prompt templates (default: `./Reference`).
- `--timeout MIN` — Per-stage timeout in minutes for LLM calls (default: 10). Increase for large files.

The full pipeline is two steps: **RAG** produces `.ragged.json` with chunked content, then **embed** generates vectors and writes `.ragged.embedded.json`. The Python `setup_qdrant.py upsert` reads the embedded file to load into Qdrant.

Required env vars:
| Var | Default | Purpose |
|-----|---------|---------|
| `LLAMA_CPP_URL` | `http://localhost:4000` | llama.cpp server endpoint (used by both `embed` and `rag`) |
| `LLAMA_CPP_MODEL` | _(none)_ | model ID string, shown in output log |
| `EMBEDDING_DIMENSION` | `0` (auto-detect) | expected vector dimension; set to avoid first-chunk mismatch errors |

All commands accept `--help` / `-h` for usage info.

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
HelpText.cs                ← centralised --help text constants and print methods
Processors/EmbeddingProcessor.cs  ← core ETL: extract chunks, call embedding service, build Qdrant points
Services/IEmbeddingService.cs   ← interface (GenerateEmbeddingAsync + EmbeddingDimension)
Services/LlamaCppEmbeddingService.cs ← real HTTP impl against llama.cpp
Services/FakeEmbeddingService.cs    ← deterministic hash-based vectors for tests
Services/RagService.cs            ← rag LLM client with timeout wrapper (WaitAsync)
Models/QdrantPoint.cs          ← record(Id, Vector, Payload)
Utils/PipelineLogger.cs        ← structured per-file debug logging (inputs, outputs, errors, flush to .log.txt)
- `Tests/`                         ← inline test harness (dotnet run)
  - Program.cs                   ← 16 PASS/FAIL assertions
Prompts/prompts.json           ← embedded prompt templates loaded as assembly resource at runtime (filesystem fallback if missing)
setup_qdrant.py                ← Qdrant collection + batch upsert (python3)
Reference/                     ← non-prompt project assets only
```

### RAG Pipeline Stages

The `rag` command runs a three-stage agentic pipeline, with each stage resetting context to avoid LLM rot:

1. **Stage 1 (Segment)** — Splits raw markdown into H2/H3 segments via LLM. Extracts actual source content from the markdown by locating headings and finding next-sibling headings. Produces `.ragged.json` wrapper with segment metadata.
2. **Stage 2 (Semantic)** — For each segment, generates rich semantic analysis (topic, keywords, entities, technologies, concepts, retrieval queries). **Deduplicates** chunks by exact `source_content` string match before proceeding. Appends a mandatory heading enumeration block to the output JSON.
3. **Stage 3 (Chunking)** — For each semantically analyzed section, produces RAG-ready chunks with: globally unique IDs (`segmentId-NNN`), verbatim `content` field, contextual `retrieval_content` field (doc title + section path + topic + content), and structured metadata. Each chunk's content is verified against source via the DATA LOSS CHECK before emission.

Each stage writes per-file debug logs (captured in `<filename>_pipeline.log.txt` in the output directory) with inputs, outputs, and errors truncated at 40k chars.

## Conventions

- **Namespaces:** mirror directory structure (`Embedding_Console.Processors`, `Embedding_Console.Services`, etc.) — underscores, not PascalCase.
- **Type system:** `<Nullable>enable</Nullable>` + `<ImplicitUsings>enable</ImplicitUsings>`. All public APIs have XML doc comments. Records for immutable data (QdrantPoint, ProcessResult, ChunkResult).
- **Async style:** `async/await` everywhere; HttpClient scoped and disposed at end of Program.cs.
- **JSON handling:** `System.Text.Json.Nodes` (`JsonNode`, `JsonObject`, `JsonArray`) — NOT the binary serializer. Output preserves original property names.
- **ANSI escapes:** Use `\u001b` Unicode literals (not `\033` octal) for color codes in console output — they are portable across Windows and Linux terminals.

## Pitfalls

1. **Tests use relative paths.** Tests assume `./Chunked Data.json` exists in the working directory. Run from the `Tests/` dir or copy the file there first.
2. **`LLAMA_CPP_URL` defaults to `:4000`, not `:6333`.** Port 4000 is llama.cpp, port 6333 is Qdrant — don't confuse them.
3. **`output.json` vs `.embedded.json`.** The Python setup script reads hardcoded `/home/clarorecto/.../output.json`. The C# app writes `<input>.embedded.json`. These are different files.
4. **Dotnet project name has a space.** `"Embedding Console.csproj"` — always quote paths containing spaces in shell commands.
5. **Dimension mismatch on first chunk.** If `EMBEDDING_DIMENSION` is set, the first embedding MUST match that dimension or `ProcessAsync` throws. Verify your model before running.
15. **Stage timeout default is 10 minutes per stage.** Large files may exceed this — use `--timeout 20` to increase. Total wall-clock time = timeout × stages × batches.
16. **Prompts loaded from embedded resource first, filesystem fallback second.** Modifying only `Reference/[PROMPT] 0N.md` files without syncing to `Prompts/prompts.json` means the compiled binary won't see changes. Run the sync script or rebuild after prompt edits.
17. **Stage 3 generates globally unique IDs (`segX-NNN`) not UUIDs.** Qdrant payloads use these IDs as `point_string_id`. Do not assume UUID format when querying.
18. **SanitizeJsonOutput escapes control chars in LLM output.** Before parsing LLM JSON responses, `SanitizeJsonOutput` (in `AgenticChunkingProcessor.cs`) now escapes literal `\n`, `\r`, and `\t` bytes inside JSON string values. LLMs sometimes emit raw newlines instead of escaped `\n` sequences (e.g., `"Vite Documentation\nbuild.modulePreload"`), causing `System.Text.Json` to throw `'0x0A' is an invalid escapable character`. The method walks the text left-to-right, tracking quote boundaries and escaping control chars only inside strings. This fix applies to both Stage 2 and Stage 3 outputs.
19. **Output `.ragged.json` now includes a `retrieval_content` field** alongside `content`. The embed command uses `content` for embedding but passes `retrieval_content` through to Qdrant payloads. If the LLM omits `retrieval_content`, the processor generates it from doc title + section path + topic + content.

## Git Commits

This repo uses a structured commit convention observed in history: `<TYPE>-<ID> : <title>` (e.g. `FEAT-3K79 : Add Qdrant collection setup script`). Generate identifiers by checking recent `git log` to avoid collisions — do not invent or reuse IDs from other branches.

### Commit Authors

All commits use the following authorship pattern, modeled after reference commit `c6a200b`:

- **Primary author:** `clarorecto54 <clarorecto54@gmail.com>`
- **Co-author (when Hermes contributes):** `Hermes Agent <hermes@nousresearch.com>`

When committing with a co-authored change, append a trailer to the commit body:

```text
Co-authored-by: Hermes Agent <hermes@nousresearch.com>
```

### Pre-Commit Analysis Checklist

- [ ] Run `git status` to see all modified files
- [ ] Run `git diff` to review changes before staging
- [ ] Verify all tests pass with `cd Tests && dotnet run`
- [ ] Update AGENTS.md if documentation was modified
- [ ] Use descriptive commit messages that explain WHAT and WHY
- [ ] Group changes logically by purpose, not by file location
- [ ] Stage specific files with `git add <path>` (never `git add .`)
- [ ] Verify commit shows clean status with `git status --short`

### Commit Format

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

### Commit Rules

- Inspect all changes (`git status`, `git diff`) before committing — do not blindly group by directory.
- Group files by logical purpose, not by when they were touched together. Independent features → separate commits.
- Stage explicitly per group: `git add path/to/file.cs` — never use `git add .` or `git add -A`.
- Verify after each commit: `git log -1 --oneline`, then `git status --short` to confirm remaining changes are untouched.
- Never stage, modify, discard, or reset unrelated working-tree changes.
- Never push — commits stay local unless the agent explicitly pushes.
