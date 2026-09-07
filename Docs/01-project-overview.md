# Project Overview

## What This Project Does

The Embedding Console is a .NET 10 console application with two main pipelines:

### 1. Embed Pipeline (JSON → Qdrant-ready)

```
JSON File (chunks)
    → ETL / Parse
    → Add "points" property to each chunk
    → Extract text content from chunks
    → Send to llama.cpp embedding server (HTTP POST /v1/embeddings)
    → Receive embedding vector (float array)
    → Build Qdrant-compatible point {id, vector, payload}
    → Store in "points" array on original chunk
    → Write enriched JSON to output file
```

The companion Python script (`setup_qdrant.py`) then reads that enriched output to create a Qdrant collection and batch-upsert all embedding points.

### 2. RAG Pipeline (Markdown → RAG-ready chunks)

```
Markdown files
    → Stage 1: Segment — split into logical segments using LLM-assisted parsing
    → Stage 2: Semantic — analyze each segment for context and meaning
    → Stage 3: Chunking — produce final RAG-ready chunks with metadata
    → Write <filename>.ragged.json per file (one per source markdown)
```

Each stage independently calls llama.cpp with `reasoning_tokens: -1` (unlimited budget). Intermediate results are saved at each stage so out-of-memory errors preserve progress. Use the `rag` subcommand to run this pipeline on one or more markdown files.

## Architecture

```
                    ┌───────────────────┐
                    │   Program.cs      │
                    │  CLI dispatch     │
                    │  embed / rag      │
                    └─────────┬─────────┘
                              │
              ┌───────────────┴───────────────┐
              │                               │
    ┌─────────▼─────────┐         ┌──────────▼──────────┐
    │  embed subcommand │         │   rag subcommand     │
    │                   │         │                      │
    │ EmbeddingProcessor│         │ AgenticChunkingProc. │
    │ EmbeddingService  │         │ RagService           │
    └─────────┬─────────┘         └──────────┬──────────┘
              │                               │
      HTTP POST /v1/embeddings      HTTP POST /v1/chat/completions
      (embeddings API)              (chat completions with reasoning)
              │                               │
              ▼                               ▼
     float[] embedding vector      JSON output per markdown file
              │                      (.ragged.json per source file)
              ▼
      QdrantPoint {id, vector, payload}
              │
              ▼
      points property on chunk
              │
              ▼
      WriteOutput → embedded.json

Companion: setup_qdrant.py → creates Qdrant collection + upserts points
```

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Use `System.Text.Json.Nodes` (not binary serializers) | Preserves unknown JSON properties, avoids DTO round-trips, mutations only add `"points"` |
| Sequential chunk processing | Controlled pace for llama.cpp; avoids overwhelming the server (can be batched later) |
|| 5-minute HTTP timeout per request | llama.cpp can take time for large chunks | Embed; rag uses 15-min timeout (reasoning tokens) |
| Deterministic FakeEmbeddingService | Tests run offline with SHA-256 based vectors — same input always gives same output |
| `IEmbeddingService` abstraction | Production uses real HTTP client; tests use fake — no network calls needed for test runs |
| QdrantPoint uses `object Vector` | Stores `float[]` but stays flexible for future changes to vector type |
| RagService uses `reasoning_tokens: -1` | Qwen model format — sends chat completions request with unlimited reasoning budget for complex prompt processing |
| JSON extraction from LLM responses | Qwen may prepend commentary before the JSON block; `ExtractJsonFromText()` collects all balanced brace/bracket blocks, sorts by span length descending (outermost first), prefers objects `{}` over arrays `[]` at equal size |

## Project Structure

```
Embedding Console.csproj          ← Main project (net10.0, System.Net.Http.Json 8.0.1)
Program.cs                        ← CLI entry: dispatch → embed/rag subcommands
HelpText.cs                       ← Centralised --help text for all commands
Processors/
    EmbeddingProcessor.cs         ← JSON ETL: extract chunks, call service, build Qdrant points
    AgenticChunkingProcessor.cs   ← RAG pipeline: Segment → Semantic → Chunk stages
Services/
    IEmbeddingService.cs          ← Interface (GenerateEmbeddingAsync + EmbeddingDimension)
    LlamaCppEmbeddingService.cs   ← Real HTTP impl against llama.cpp /v1/embeddings
    FakeEmbeddingService.cs       ← Deterministic hash-based vectors for tests
    IRagService.cs                ← Interface for RAG prompt sending
    RagService.cs                 ← Chat completions with reasoning_tokens, JSON extraction
    AgenticChunkingOptions.cs     ← Configuration: max tokens, output dir, prompt dir, URL
Models/
    QdrantPoint.cs                ← Record(Id, Vector, Payload)
Tests/                            ← Inline test harness (dotnet run)
    Program.cs                    ← 15 PASS/FAIL assertions
    Tests.csproj                  ← ProjectReference to main project
setup_qdrant.py                   ← Qdrant collection creation + batch upsert
Docs/                             ← This documentation directory
Reference/                        ← Prompts and sample data used during development
```

## Data Model: Input vs Output

**Input JSON** (`Chunked Data.json`) — 33 chunks with properties like `id`, `content`, `metadata`:
```json
{
  "chunks": [
    {
      "id": "build-options-build-target-001",
      "content": "...",
      "metadata": { ... }
    }
  ]
}
```

**Output JSON** (`*.embedded.json`) — same structure with added `points` array per chunk:
```json
{
  "chunks": [
    {
      "id": "build-options-build-target-001",
      "content": "...",
      "metadata": { ... },
      "points": [
        {
          "id": "build-options-build-target-001",
          "vector": [0.123, -0.456, 0.789, ...],
          "payload": { "id": "...", "content": "...", "metadata": { ... } }
        }
      ]
    }
  ]
}
```

## Related Documents

- [Getting Started](02-getting-started.md) — environment setup and build/run commands
- [Core Components](03-core-components.md) — detailed class reference for processors and models
- [Embedding Services](04-embedding-services.md) — service interface, HTTP client, and fake implementation
- [Qdrant Integration](05-qdrant-integration.md) — collection schema and upsert workflow
- [CLI Reference](06-cli-reference.md) — command-line arguments for embed and rag subcommands
- [RAG Pipeline Docs](04-rag-pipeline.md) — agentic chunking: Segment → Semantic → Chunk stages
