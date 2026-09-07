# RAG Pipeline Docs

## Overview

The `rag` subcommand implements an **agentic chunking pipeline** that processes markdown files into RAG-ready chunks through three LLM-driven stages. Each stage calls the chat completions API separately, resetting context to avoid "rotting" (context degradation over long chains).

```
Markdown file
    │
    ├─> (if > maxChunkTokens) Chunk by token budget → multiple batches
    │
    ▼
Stage 1: Segment
    Input: raw markdown (or batch)
    Output: SegmentedFile[] — title, heading_path, filename per segment
    Prompt: "01 Segment.md"
    │
    ▼
Stage 2: Semantic Extraction
    Input: each SegmentedFile's extracted source_content
    Output: chunks[] with metadata (topics, entities, technologies, keywords)
    Prompt: "02 Semantic.md"
    └─> Saves semantic_groups.json to output dir (for OOM resumption)
    │
    ▼
Stage 3: RAG Chunking
    Input: each Stage-2 chunk with document context + section metadata
    Output: final chunks[] with id, content, metadata
    Prompt: "03 Chunking.md"
    └─> Saves segment_output.json to output dir (for OOM resumption)
    │
    ▼
Output: <filename>.ragged.json
```

## RagService — Chat Completions Client

**File:** `Services/RagService.cs`

RagService wraps an OpenAI-compatible chat completions endpoint. Unlike the embedding service, it uses `POST /v1/chat/completions` with system/user message separation and reasoning token support.

### API Request Format

| Field | Value | Notes |
|-------|-------|-------|
| `model` | `""` (empty) | llama.cpp ignores this; required for OpenAI compatibility |
| `messages` | `[system, user]` array | Lines starting with `# ` at prompt top → system role; rest → user role |
| `temperature` | `0.1` | Deterministic output — minimizes randomness in structured responses |
| `top_p` | `0.9` | Nucleus sampling for focused generation |
| `max_tokens` | `-1` | Unlimited response length |
| `reasoning_tokens` | `-1` | Max reasoning budget (Qwen format) |

HTTP timeout: **15 minutes** (configured in constructor — reasoning-heavy prompts can take time).

### Response Parsing

```csharp
// Prefer reasoning_content first (if model produces it), fall back to content
rawContent = response.reasoning_content ?? response.content;
content = ExtractJsonFromText(rawContent);  // strips commentary, finds valid JSON block
```

The model's system prompt enforces: *"Return ONLY valid JSON — no markdown fences, no explanations, no commentary."* In practice, Qwen still sometimes prepends reasoning text, which `ExtractJsonFromText` handles.

### JSON Extraction Algorithm (`ExtractJsonFromText`)

When the LLM response contains text before/after JSON (e.g., `"Here's my thinking: ...\n{ ... }"`), this method extracts the valid JSON block:

1. **Fast path:** Try parsing the entire response as JSON — succeeds if clean.
2. **Block collection:** Scan for all balanced `{}` and `[]` blocks, recording start/end positions.
3. **Sorting:** Sort candidates by span length descending (outermost first). At equal size, prefer `{}` objects over `[]` arrays (stage outputs are objects).
4. **Validation:** Try parsing each candidate in order — return the first valid JSON.
5. **Fallback:** Return text from first opening brace to last closing bracket.

This handles:
- LLM reasoning preamble before JSON
- Markdown code fences wrapping the JSON (` ```json ... ``` `)
- Multiple nested objects (e.g., template examples with `"..."` placeholders)
- Mixed `{}` and `[]` blocks in responses

### Error Handling

```csharp
// RagServiceException wraps HTTP errors with status code
throw new RagServiceException(
    $"Chat endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {errorBody}",
    (int)response.StatusCode);

// Missing choices or message field
"No choices in chat completion response — invalid format."
"Response missing 'message' field — invalid format."
```

### Disposal

RagService implements `IDisposable` — it owns the `HttpClient` and disposes it on `Dispose()`. The AgenticChunkingProcessor also implements `IDisposable` but doesn't call `ragService.Dispose()` since the service is typically injected externally (owned by Program.cs's HTTP scope).

## AgenticChunkingProcessor — Stage Orchestration

**File:** `Processors/AgenticChunkingProcessor.cs`

Orchestrates the three-stage pipeline. Each stage calls `RagService.SendAsync()` with a loaded prompt template, parses the JSON response, and passes results to the next stage.

### Constructor Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `ragService` | (required) | Injected RagService instance for LLM calls |
| `promptDir` | `"./Reference"` | Directory containing prompt template files |
| `maxChunkTokens` | `8000` | Token budget per markdown batch and Stage 3 input |
| `outputDir` | `"./rag_output"` | Where `.ragged.json` and intermediate files are written |
| `llamaTimeout` | `TimeSpan.FromMinutes(5)` | Per-stage timeout (used via CancellationTokenSource) |

### Pipeline Flow

#### Pre-processing: Markdown Chunking by Token Budget

If a file exceeds `_maxChunkTokens`:
- Splits at double-newline boundaries (paragraph level)
- Code blocks use ~6 chars/token; plain text uses ~4 chars/token estimate
- Single paragraphs exceeding the limit are split by newline within them
- Each batch prepended with `[Document Part N/M]` header for LLM context

```csharp
// Example: 50k-char file → ~8 batches of ~8k tokens each
if (estimatedTokens > _maxChunkTokens) {
    markdownBatches = ChunkMarkdown(rawMarkdown, _maxChunkTokens);
}
```

#### Stage 1: Document Segmentation

- Loads prompt: `"{PromptDir}/01 Segment.md"`
- Sends full prompt with markdown content as user message
- LLM returns JSON with `"segments"` array containing `{id, filename, title, heading_path}` for each logical section
- **Source content extraction:** After parsing segment metadata, the processor locates each segment's actual text in the source markdown by searching for `## {Title}` or `# {Title}` headings, then extracts until the next heading at same/lower level.

Stage 1 output: `List<SegmentedFile>`

#### Stage 2: Semantic Extraction

- Loads prompt: `"{PromptDir}/02 Semantic.md"`
- For each segmented file:
  - Sends its extracted source content to LLM
  - Parses JSON response — expects `"chunks"` array
  - Each chunk contains metadata: `topic`, `summary`, `keywords`, `entities`, `technologies`, `heading_path`
- Saves intermediate `semantic_groups.json` to output dir for OOM resumption

Console output per segment: `[N/M] Processing: {Title}... ✓` or `[N/M] Processing: {Title}... ✗ ({error})`

Stage 2 output: `List<(string segmentId, string markdownContent, JsonObject analysis)>`

#### Stage 3: RAG Chunking

- Loads prompt: `"{PromptDir}/03 Chunking.md"`
- For each semantic group from Stage 2:
  - Builds structured input with document context (title, source, type, topic, summary) + section metadata (heading path, keywords, entities, technologies) + raw markdown content
  - Sends to LLM for final chunk boundaries
  - Parses response — expects `"chunks"` array or bare array
  - Wraps each chunk in `{id, content, metadata}` format matching Chunked Data.json schema

Console output every 5 sections: `Processed N semantic sections, M final chunks so far...`

Stage 3 output: `List<JsonObject>` (final chunks)

### Output Format

Each processed file produces a `.ragged.json`:

```json
{
  "source_file": "Reference/Project Structure.md",
  "document_title": "Project Structure",
  "stage1_segments": 12,
  "stage2_semantic_chunks": 47,
  "stage3_final_chunks": 89,
  "processing_time_seconds": 67.3,
  "chunks": [
    {
      "id": "project-structure-chunk-001",
      "content": "...",
      "metadata": {
        "source_file": "Project Structure.md",
        "topic": "...",
        "heading_path": ["Processors", "AgenticChunkingProcessor"]
      }
    }
  ]
}
```

### Intermediate Files for OOM Resumption

The processor saves intermediate results to `_outputDir`:
- `segment_output.json` — after Stage 1 (Segment) completes
- `semantic_groups.json` — after Stage 2 (Semantic Grouping) completes

On restart, if intermediates exist, the pipeline skips completed stages. If Chunk stage fails with OOM:
```
No intermediate results found — restarting from Segment
```

### Prompt File Convention

Prompt templates are loaded from `_promptDir`:
- `01 Segment.md` — Document segmentation instructions + JSON schema for segments array
- `02 Semantic.md` — Semantic analysis: topic extraction, entity identification, keyword generation
- `03 Chunking.md` — Final RAG chunk boundaries with metadata preservation

Each prompt is formatted as a full user message. The processor appends:
```markdown
{prompt_template}

---

# SOURCE MARKDOWN

```
{markdown_content}
```

Return ONLY valid JSON.
```

The RagService system role adds: `"You are a precise markdown processing agent. Return ONLY valid JSON — no markdown fences, no explanations, no commentary."`

### Markdown Code Fence Handling (`SanitizeJsonOutput`)

LLMs sometimes wrap responses in markdown code fences (```). This method strips them:

1. Remove leading ``` if present (including language specifier line)
2. Remove trailing ``` if present

After stripping, the cleaned text is parsed with `JsonNode.Parse()`. If parsing fails, an `AgenticChunkingException` is thrown with raw response snippet for debugging.

### Stage Error Messages

| Stage | Exception Message Format |
|-------|-------------------------|
| Segment | `"Stage 1 parse error: {message}\n\nRaw:\n{raw_500_chars}"` |
| Semantic | `"Stage 2 parse error: {message}\n\nRaw:\n{raw_500_chars}"` |
| Chunking | `"Stage 3 parse error: {message}\n\nRaw:\n{raw_500_chars}"` |
| Timeout (any) | `"{stage} timed out after Nm."` |

### Console Output

Per-file pipeline header:
```
=== Agentic Chunking: Project Structure ===
  Input:    Reference/Project Structure.md (2,838 chars)
  If > maxChunkTokens: Chunking markdown into ~8000 token batches...
```

Stage transitions:
```
→ Stage 1: Document Segmentation
  Segments: 12

→ Stage 2: Semantic Extraction
  [1/12] Processing: Build System... ✓
  [2/12] Processing: Project Structure... ✗ (parse error)
  ...

→ Stage 3: RAG Chunking
  Processed 5 semantic sections, 23 final chunks so far...
```

Completion summary:
```
  Output:    ./rag_output/project-structure.ragged.json
  Final chunks: 89
  Time:          67.3s
```

## Related Documents

- [Project Overview](01-project-overview.md) — architecture context for rag vs embed pipelines
- [CLI Reference](06-cli-reference.md) — rag subcommand arguments and options
- [Core Components](03-core-components.md) — RagService, AgenticChunkingProcessor class reference
- [Troubleshooting](09-troubleshooting.md) — Stage 1/2/3 parse errors, OOM resumption, reasoning token format
