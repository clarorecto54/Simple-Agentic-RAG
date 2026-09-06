# Embedding Console — Documentation Index

A .NET 10 C# console application that reads a JSON file containing text chunks, generates embeddings via a llama.cpp server (OpenAI-compatible `/v1/embeddings` endpoint), writes enriched output back with Qdrant-ready points, and includes a companion Python script for collection setup and batch upsert.

## Table of Contents

| # | Document | Description |
|---|----------|-------------|
| 1 | [Project Overview](01-project-overview.md) | Architecture, pipeline flow, design principles, key decisions |
| 2 | [Getting Started](02-getting-started.md) | Environment setup, build, run, environment variables |
| 3 | [Core Components](03-core-components.md) | EmbeddingProcessor, QdrantPoint, models, internal records |
| 4 | [Embedding Services](04-embedding-services.md) | IEmbeddingService interface, LlamaCppEmbeddingService, FakeEmbeddingService |
| 5 | [Qdrant Integration](05-qdrant-integration.md) | Collection schema, vector params, quantization, upsert workflow |
| 6 | [CLI Reference](06-cli-reference.md) | Command-line arguments, output paths, auto-naming conventions |
| 7 | [Testing](07-testing.md) | Inline test suite (15 assertions), FakeEmbeddingService usage, test scenarios |
| 8 | [Prompts & RAG Pipeline](08-prompts-reference.md) | Original spec + segmentation/semantic/chunking agent prompts |
| 9 | [Troubleshooting](09-troubleshooting.md) | Common errors, pitfall list, dimension mismatch, HTTP failures |

## Quick Navigation

- **New here?** → Start with [Project Overview](01-project-overview.md), then [Getting Started](02-getting-started.md)
- **Want to run it?** → [Getting Started](02-getting-started.md)
- **Need to know the data flow?** → [Core Components](03-core-components.md)
- **Configuring llama.cpp connection?** → [Embedding Services](04-embedding-services.md)
- **Setting up Qdrant?** → [Qdrant Integration](05-qdrant-integration.md)
- **Running tests?** → [Testing](07-testing.md)
