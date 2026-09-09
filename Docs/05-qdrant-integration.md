# Qdrant Integration

This document covers the Qdrant collection configuration, vector parameters, quantization strategy, and the upsert workflow via `setup_qdrant.py`.

## Collection Overview

**Collection Name:** `Coding Knowledge`  
**Script:** `setup_qdrant.py` (argparse-based CLI with subcommands)
**Input Source:** JSON file path supplied via `--json-path` (defaults unspecified by script)

The script now uses two subcommands — `create` and `upsert` — with argparse. It is no longer fully idempotent; each subcommand must be run separately. See the CLI table below for full command-line usage.

## CLI Reference

```bash
python3 setup_qdrant.py --help
python3 setup_qdrant.py create --help
python3 setup_qdrant.py upsert --help
```

### Global Options

| Flag | Default | Purpose |
|------|---------|---------|
| `--collection`, `-c` | `"Coding Knowledge"` | Target Qdrant collection name |
| `--qdrant-url` | `http://localhost:6333` | Qdrant server URL |

### `create` Subcommand

Queries llama.cpp for the active embedding model's dimension (`n_embd`) before creating the collection.

```bash
# Auto-detect dimension from llama.cpp
python3 setup_qdrant.py create --llama-url http://localhost:4000

# Manual dimension override (skips llama.cpp query)
python3 setup_qdrant.py create --dimension 4096
```

| Flag | Default | Required | Purpose |
|------|---------|----------|---------|
| `--llama-url`, `-l` | _(none)_ | Conditional | llama.cpp server URL; queried via `/v1/models` to auto-detect model dimension. If omitted, `--dimension` is required. |
| `--dimension`, `-d` | `0` | If no `--llama-url` | Force the vector dimension. When provided without `--llama-url`, llama.cpp query is skipped entirely. |

The create flow:
1. Query `http://<llama-url>/v1/models` and extract `n_embd` (or `embedding_dim`) from the model object.
2. Verify the dimension (exits if field is missing).
3. Delete any existing collection with the target name (idempotent per-collection).
4. Create the collection with float32 vectors, COSINE distance, TurboQuant 4-bit.

### `upsert` Subcommand

Loads points from a JSON file and inserts them into an existing collection. The collection must already exist (run `create` first).

```bash
python3 setup_qdrant.py upsert -j ./output.json
python3 setup_qdrant.py upsert -j ./output.embedded.json -c MyCollection
```

| Flag | Required | Purpose |
|------|----------|---------|
| `--json-path`, `-j` | Yes | Path to the JSON file containing embedded chunks (`chunks[].points[]`) |

The upsert flow:
1. Verify the target collection exists (exits if not).
2. Parse the JSON and extract all valid points (those with both `"id"` and `"vector"` fields).
3. Upsert in batches of 100.
4. Report point count and print collection config for verification.

The script prints a warning if the collection's actual dimension differs from the dimension used during creation.

## Collection Configuration

### Vector Parameters

```python
vectors_config = models.VectorParams(
    size=vector_size,        # dynamic — determined by create subcommand (--llama-url or --dimension)
    distance=models.Distance.COSINE,   # cosine similarity
    datatype=models.Datatype.FLOAT32,  # full precision
    memory=models.Memory.CACHED,       # vectors in RAM (on_disk=False)
)
```

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| `size` | dynamic (e.g. 4096) | Matches the active embedding model's output dimension — auto-detected from llama.cpp `/v1/models` or supplied via `--dimension` |
| `distance` | COSINE | Standard for cosine-normalized embeddings |
| `datatype` | FLOAT32 | Full precision — safe for small datasets (~512 KB for 33 points) |
| `memory` | CACHED | Original vectors kept in RAM; no disk I/O at query time |

### Optimizer Configuration

```python
optimizers_config = models.OptimizersConfigDiff(
    default_segment_number=1,     # single segment — no merge overhead at query time
    max_segment_size=50_000,      # far exceeds 33 points — never splits
    indexing_threshold=5_000,     # index already ready — no lazy rebuild
)
```

All parameters are tuned for a tiny dataset (33 points): single segment eliminates multi-segment merge overhead, and the index is always warm.

### HNSW Graph Configuration

```python
hnsw_config = models.HnswConfigDiff(
    m=6,              # optimal fan-out for small collections — compact traversal
    ef_construct=128,  # build-time accuracy — sufficient for cardinality of 33
    on_disk=False,     # HNSW graph in RAM — no disk I/O
)
```

### Quantization Configuration

```python
quantization_config = models.TurboQuantization(
    turbo=models.TurboQuantQuantizationConfig(
        bits=models.TurboQuantBitSize.BITS4,
        memory=models.Memory.PINNED,   # quantized index pinned in RAM
    ),
)
```

**TurboQuant 4-bit:** Compresses the search index by ~4x. Original float32 vectors are kept on disk for rescoring, so recall is within ~1-2 percentage points of raw float32 at 4x throughput. Memory is pinned (never swapped out).

## Upsert Workflow

### Step 1: Extract Points from Embedded JSON

The script reads the embedded JSON and iterates over all chunks:

```python
for chunk in data.get("chunks", []):
    payload = {
        "string_id": chunk["id"],
        "content": chunk["content"],
        "retrieval_content": chunk.get("retrieval_content"),
        "metadata": chunk.get("metadata", {}),
    }

    for point_entry in chunk.get("points", []):
        all_points.append(models.PointStruct(
            id=next_id,
            vector=point_entry["vector"],
            payload=payload | {"point_string_id": point_str_id},
        ))
```

Each `QdrantPoint` stored in the `"points"` array is flattened into a single `PointStruct`:
- **id** — sequential integer (0, 1, 2, ...) for deterministic ordering
- **vector** — direct extraction from the embedding point's `"vector"` field
- **payload** — combines chunk-level metadata with the point's own string ID

### Step 2: Batch Upsert

Points are upserted in batches of 100 (safety net for larger payloads):

```python
batch_size = 100
for i in range(0, len(all_points), batch_size):
    batch = all_points[i : i + batch_size]
    client.upsert(collection_name=COLLECTION_NAME, points=batch)
```

### Step 3: Verification

The script runs a self-search test: it queries the collection with the first point's own vector and confirms the top hit is itself (or a nearby point).

## Payload Structure in Qdrant

Each upserted point carries this payload:

```json
{
  "string_id": "build-options-build-target-001",
  "content": "... original chunk text ...",
  "retrieval_content": "... structured path + topic + content excerpt ...",
  "metadata": { "source": "..." },
  "point_string_id": "build-options-build-target-001"
}
```

The `content` field contains the verbatim source text for embedding, while `retrieval_content` provides structured search context (`Doc Title » Section Path — topic: ...`). The combination enables both dense vector retrieval and rich metadata filtering. The payload also includes `string_id` (original chunk ID) and `point_string_id` (Qdrant point identifier).

## Collection Status Output

After creation and upsert, the script prints:

```
Point count in 'Coding Knowledge': 33

Collection configuration:
  Status:     green (ready)
  Vectors:    4096 dims, distance=COSINE
  Datatype:   FLOAT32
  Quantised:  TurboQuant (4-bit search index, rescoring against float32)

Self-search test: found 5 result(s)
  Top hit id=X  score=1.0000
```

## Related Documents

- [Project Overview](01-project-overview.md) — why Qdrant points are embedded in JSON
- [Core Components](03-core-components.md) — the `QdrantPoint` record structure
- [Embedding Services](04-embedding-services.md) — how vectors reach the `"points"` array
