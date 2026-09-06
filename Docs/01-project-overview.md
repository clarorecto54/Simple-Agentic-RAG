# Project Overview

## What This Project Does

The Embedding Console is a .NET 10 console application that transforms chunked JSON data into a Qdrant-ready format:

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

## Architecture

```
                    ┌───────────────────┐
                    │   Program.cs      │
                    │  CLI + Config     │
                    └─────────┬─────────┘
                              │
                              ▼
                    ┌───────────────────┐
                    │ EmbeddingProcessor│
                    │  (ProcessAsync)   │
                    │  ExtractChunks    │
                    │  BuildQdrantPoint │
                    └─────────┬─────────┘
                              │
                        Extract text
                              │
                              ▼
                    ┌───────────────────┐
                    │ IEmbeddingService │
                    │ (abstraction)     │
                    └─────────┬─────────┘
                              │
                  ┌───────────┴───────────┐
                  │                       │
          ┌───────▼───────┐       ┌──────▼───────┐
          │ LlamaCpp      │       │ Fake         │
          │ Embedding     │       │ Embedding    │
          │ Service       │       │ Service      │
          └───────┬───────┘       └──────────────┘
                  │
          HTTP POST /v1/embeddings
          (OpenAI-compatible API)
                  │
                  ▼
          float[] embedding vector
                  │
                  ▼
         QdrantPoint {id, vector, payload}
                  │
                  ▼
         points property on chunk
                  │
                  ▼
         WriteOutput → embedded.json
```

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Use `System.Text.Json.Nodes` (not binary serializers) | Preserves unknown JSON properties, avoids DTO round-trips, mutations only add `"points"` |
| Sequential chunk processing | Controlled pace for llama.cpp; avoids overwhelming the server (can be batched later) |
| 5-minute HTTP timeout per request | llama.cpp can take time for large chunks |
| Deterministic FakeEmbeddingService | Tests run offline with SHA-256 based vectors — same input always gives same output |
| `IEmbeddingService` abstraction | Production uses real HTTP client; tests use fake — no network calls needed for test runs |
| QdrantPoint uses `object Vector` | Stores `float[]` but stays flexible for future changes to vector type |

## Project Structure

```
Embedding Console.csproj          ← Main project (net10.0, System.Net.Http.Json 8.0.1)
Program.cs                        ← CLI entry: args → env config → processor pipeline
Processors/
    EmbeddingProcessor.cs         ← Core ETL: extract chunks, call service, build points
Services/
    IEmbeddingService.cs          ← Interface (GenerateEmbeddingAsync + EmbeddingDimension)
    LlamaCppEmbeddingService.cs   ← Real HTTP impl against llama.cpp /v1/embeddings
    FakeEmbeddingService.cs       ← Deterministic hash-based vectors for tests
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
- [CLI Reference](06-cli-reference.md) — command-line arguments and output path conventions
