# Core Components

This document covers the data models, processor classes, and internal records that drive the ETL pipeline.

## QdrantPoint (Models)

**File:** `Models/QdrantPoint.cs`

```csharp
public record QdrantPoint(
    string Id,
    object Vector,
    JsonNode Payload);
```

A simple immutable record representing a point compatible with Qdrant's upsert API:
- **Id** — deterministic identifier from the source chunk (e.g., `"build-options-build-target-001"`)
- **Vector** — stores `float[]` embedding; typed as `object` for flexibility
- **Payload** — all original chunk properties, cloned via `DeepClone()`, excluding only `"points"`

## EmbeddingProcessor (Processors)

**File:** `Processors/EmbeddingProcessor.cs`

### ChunkResult

Result of processing a single chunk:
```csharp
public record ChunkResult(
    string ChunkId,
    string ContentKey,
    bool Success,
    string? Error,
    QdrantPoint? Point = null);
```

Tracks success/failure per chunk for reporting and test verification.

### ProcessResult

Result of a full processing run:
```csharp
public record ProcessResult(
    JsonNode RootNode,
    string InputPath,
    IReadOnlyList<ChunkResult> Results,
    int FailedCount);
```

Holds the mutated root node (with `"points"` added), path references, per-chunk results, and failure count.

### Key Methods

#### ProcessAsync (async)

Orchestrates the full pipeline:
1. Calls `ExtractChunks()` to find the `"chunks"` array (or falls back to first array of objects)
2. Adds an empty `"points": []` array to each chunk node
3. Iterates chunks sequentially, sending content through `IEmbeddingService.GenerateEmbeddingAsync()`
4. Validates dimension against `_expectedDimension` on the first successful call
5. Calls `BuildQdrantPoint()` and serializes it into each chunk's `"points"` array
6. Reports progress every 10 chunks or at completion

**Error handling:** Individual chunk failures are caught and logged; processing continues with remaining chunks. Failed chunk IDs are reported in the summary.

#### WriteOutput (static)

Serializes the mutated `JsonNode` to disk with original property names preserved:
```csharp
public static void WriteOutput(ProcessResult result, string outputPath)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,  // Preserve original property names
    };
    var jsonStr = result.RootNode.ToJsonString(options);
    File.WriteAllText(outputPath, jsonStr);
}
```

### Internal Helpers

| Method | Purpose |
|--------|---------|
| `ExtractChunks(JsonNode)` | Locates the `"chunks"` array at root; falls back to scanning all root values for arrays of objects |
| `ExtractContentKey(JsonObject)` | Detects the content field — tries `content`, `text`, `body`, `data` in order, then any long string property |
| `BuildQdrantPoint(ChunkInfo, float[])` | Creates QdrantPoint with chunk ID, embedding vector, and cloned payload (excluding `"points"`) |
| `BuildPointJson(QdrantPoint)` | Serializes a QdrantPoint into a JsonObject with `id`, `vector` array, and `payload` properties |
| `PrintProgress(...)` | Console output with ANSI color codes — white for Input, yellow for status, gray for progress, green for results |

## Internal Records (non-public)

These are private helper types within `EmbeddingProcessor.cs`:

### ChunkInfo

```csharp
private record ChunkInfo(string ChunkId, JsonNode ChunkNode, string? ContentKey);
```

Used during processing to track each chunk's ID, the mutable JSON node, and which property contains the embeddable text.

## Data Flow Summary

```
JSON root node (JsonNode)
    │
    ├─> ExtractChunks → List<ChunkInfo>
    │       │
    │       └─> for each ChunkInfo:
    │               ChunkNode["points"] = new JsonArray()  // placeholder
    │
    ├─> for each ChunkInfo:
    │       text = ExtractText(ChunkInfo)   // reads content field
    │       vector = service.GenerateEmbeddingAsync(text)
    │       point = BuildQdrantPoint(ChunkInfo, vector)
    │       ChunkNode["points"].Add(BuildPointJson(point))
    │
    └─> WriteOutput(result, outputPath)  // serializes mutated JsonNode
```

## Related Documents

- [Project Overview](01-project-overview.md) — architecture context
- [Embedding Services](04-embedding-services.md) — how the processor calls into IEmbeddingService
- [Qdrant Integration](05-qdrant-integration.md) — how points flow from here into Qdrant
