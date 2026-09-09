# Prompts & RAG Pipeline

This document collects the prompts and spec files used during the project's development. They define the three-agent RAG preprocessing pipeline that feeds into the embedding system.

## Overview: The Three-Agent RAG Pipeline

The original spec (`[DEV] Initial Prompt.md`) describes a three-stage agent pipeline for processing Markdown documentation into Qdrant-ready chunks:

```
Source Markdown Document
    │
    ├─► Agent 1: Segmentation (01 Segment)
    │     Splits large documents into semantically coherent files
    │     Output: JSON segmentation plan
    │
    ├─► Agent 2: Semantic Extraction (02 Semantic)
    │     Analyzes segmented files — entities, concepts, relationships
    │     Output: JSON with document metadata + semantic chunking plan
    │
    └─► Agent 3: Chunking + Metadata (03 Chunking)
          Final embedding-ready chunks with retrieval metadata
          Output: JSON ready for Qdrant upsert

                    ↓ EMBEDDING STAGE (C# app) ↓

Embedding vectors → points array → Qdrant batch upsert
```

## Reference Files

### `[RAG] 01 Segment.md` — Document Segmentation Agent

**Purpose:** Split a source Markdown document into multiple smaller files for downstream RAG processing.

**Key rules:**
- Preserve original content exactly — do NOT rewrite, summarize, or correct
- Respect heading hierarchy as structural boundaries
- Never split fenced code blocks, Markdown tables, or logically connected lists
- Output: JSON with `source_file`, `segments` (each having `id`, `filename`, `title`, `heading_path`, `start_marker`, `end_marker`, `reason`)

**Filename convention:** Lowercase kebab-case. Deterministic and filesystem-safe.

### `[RAG] 02 Semantic.md` — Semantic Extraction Agent

**Purpose:** Analyze a segmented Markdown document deeply to produce structured metadata suitable for semantic chunking, embedding, and Qdrant indexing.

**Key outputs per chunk:**
- `chunk_id` — deterministic identifier
- `title`, `heading_path`, `topic`, `summary`
- `keywords`, `entities`, `concepts`, `relationships`
- `retrieval_topics` — questions this chunk can answer
- `code_languages` — programming languages present
- `source_content` — exact original Markdown (preserved verbatim)

**Output format:** JSON with `document` metadata and `chunks[]` array. No embeddings generated here — that's the next agent's job.

### `[RAG] 03 Chunking.md` — Chunking + Metadata Agent

**Purpose:** Take already-semantic-analyzed sections and produce final embedding-ready chunks with retrieval metadata. This is the downstream stage after segmentation and semantic extraction.

**Key rules:**
- Target 300–800 tokens per chunk, prefer 400–600
- No overlap by default; only use when preserving context across boundaries
- Semantic boundaries over fixed token counts
- Source content must be EXACT original Markdown (no rewriting)
- **Two-content model:** Each chunk MUST include both `content` (verbatim source for embedding) and `retrieval_content` (structured path + topic + content excerpt for search context). If the LLM omits `retrieval_content`, the C# processor generates it from doc title + section path + topic + content.
- **DATA LOSS CHECK:** Verify each chunk's content against source markdown before emission — ensure no truncation or rewriting occurred.

**Output format:** JSON with `chunks[]`, each containing `id` (globally unique `segX-NNN`), `content`, `retrieval_content`, and full `metadata` object:

**Qdrant-ready output structure:**
```json
{
  "chunks": [
    {
      "id": "seg0-001",
      "content": "EXACT ORIGINAL MARKDOWN",
      "retrieval_content": "Document Title » Section Path — topic: ... content excerpt...",
      "metadata": {
        "source_file": "...",
        "document_title": "...",
        "section": "...",
        "heading_path": [],
        "topic": "...",
        "summary": "...",
        "keywords": [],
        "entities": [],
        "technologies": [],
        "concepts": [],
        "retrieval_queries": [],
        "code_languages": []
      }
    }
  ]
}
```

Note: The Qdrant payload includes a `point_string_id` field (the chunk's unique ID) in addition to the above. See [Qdrant Integration](05-qdrant-integration.md) for full payload details.

### `[DEV] Initial Prompt.md` — Original Implementation Spec

**Purpose:** The comprehensive spec that defined the C# console application. Contains 30 requirements covering CLI input, ETL stage, llama.cpp HTTP client, embedding service abstraction, Qdrant point structure, error handling, test requirements, and architecture.

## Prompt Development Notes

The prompts follow a consistent pattern:
1. **Role definition** — "You are a [type] agent"
2. **Primary objective** — single sentence describing the job
3. **Input format** — what arrives from the previous stage
4. **Rules** — what to preserve, what NOT to do
5. **Output schema** — exact JSON structure expected
6. **Validation checklist** — final verification before output

This consistency makes it easy to extend or add new agents in the pipeline.

## Related Documents

- [Project Overview](01-project-overview.md) — how these prompts relate to the C# implementation
- [Core Components](03-core-components.md) — the Qdrant point structure matches this output schema
- [Qdrant Integration](05-qdrant-integration.md) — how chunks flow from prompt output into the collection
