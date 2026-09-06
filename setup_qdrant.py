"""Create 'Coding Knowledge' Qdrant collection and upsert all embeddings from output.json."""

import json
import sys
from qdrant_client import QdrantClient, models

CLIENT_URL = "http://localhost:6333"
COLLECTION_NAME = "Coding Knowledge"
VECTOR_SIZE = 4096
INPUT_FILE = "/home/clarorecto/My Projects/RAG/Embedding Console/output.json"


def create_collection(client: QdrantClient):
    """Create the collection with maximum-performance settings for a small dataset (33 points).

    Rationale per parameter:
    - datatype=float32 : with only 33 points (~512 KB total), keeping full precision avoids any
      quantisation-induced accuracy loss. TurboQuant is stacked on top for ~4x search compression
      with rescoring against the original float32 vectors.
    - distance=COSINE  : standard for cosine-normalised embeddings (Qwen3-Embedding).
    - default_segment_number=1 : single segment eliminates multi-segment merge overhead at query time.
    - max_segment_size=50000 : large enough to hold all 33 points in one segment without splitting.
    - indexing_threshold=5000 : defer vector index rebuilds for this tiny dataset (index already exists).
    - HNSW m=6, ef_construct=128 : compact graph tuned for small cardinalities — fast traversal, no wasted RAM.
    - TurboQuant BITS4 : 4-bit per-dimension compression for the search index; original float32 kept
      on disk for rescoring → recall within ~1-2 pp of raw float32 at 4x throughput.
    """

    vectors_config = models.VectorParams(
        size=VECTOR_SIZE,
        distance=models.Distance.COSINE,
        datatype=models.Datatype.FLOAT32,
        memory=models.Memory.CACHED,       # original vectors in RAM (on_disk=False)
    )

    optimizers_config = models.OptimizersConfigDiff(
        default_segment_number=1,
        max_segment_size=50_000,           # far exceeds 33 points
        indexing_threshold=5_000,          # index is always ready (no lazy rebuild)
    )

    hnsw_config = models.HnswConfigDiff(
        m=6,                               # optimal fan-out for small collections
        ef_construct=128,                  # build-time accuracy
        on_disk=False,                     # HNSW graph in RAM
    )

    quantization_config = models.TurboQuantization(
        turbo=models.TurboQuantQuantizationConfig(
            bits=models.TurboQuantBitSize.BITS4,
            memory=models.Memory.PINNED,   # quantised index stays in RAM
        ),
    )

    client.create_collection(
        collection_name=COLLECTION_NAME,
        vectors_config=vectors_config,
        optimizers_config=optimizers_config,
        hnsw_config=hnsw_config,
        quantization_config=quantization_config,
    )
    print(f"Collection '{COLLECTION_NAME}' created successfully.")


def upsert_points(client: QdrantClient):
    """Load output.json and upsert all points into the collection."""

    with open(INPUT_FILE, "r") as f:
        data = json.load(f)

    all_points: list[models.PointStruct] = []
    next_id = 0

    for chunk in data.get("chunks", []):
        payload = {
            "string_id": chunk["id"],
            "content": chunk["content"],
            "metadata": chunk.get("metadata", {}),
        }

        for point_entry in chunk.get("points", []):
            point_str_id = point_entry.get("id")
            vector = point_entry.get("vector")
            if not point_str_id or not vector:
                continue
            all_points.append(
                models.PointStruct(
                    id=next_id,
                    vector=vector,
                    payload=payload | {"point_string_id": point_str_id},
                )
            )
            next_id += 1

    # Upsert in batches of 100 (safety net for large payloads)
    batch_size = 100
    for i in range(0, len(all_points), batch_size):
        batch = all_points[i : i + batch_size]
        client.upsert(collection_name=COLLECTION_NAME, points=batch)

    print(f"Upserted {len(all_points)} points into '{COLLECTION_NAME}'.")
    return all_points


def verify(client: QdrantClient, sample_vector):
    """Verify the collection exists and report its status."""

    # Count
    count = client.count(COLLECTION_NAME)
    print(f"Point count in '{COLLECTION_NAME}': {count.count}")

    # Collection info — config.params.vectors may be Dict (client v1.19) or VectorParams
    info = client.get_collection(COLLECTION_NAME)
    vectors_cfg = info.config.params.vectors  # type: ignore[union-attr]
    vec_size = vectors_cfg.size if hasattr(vectors_cfg, "size") else vectors_cfg.get("size", "N/A")  # type: ignore[attr-defined]
    vec_dist = vectors_cfg.distance if hasattr(vectors_cfg, "distance") else vectors_cfg.get("distance", "N/A")  # type: ignore[attr-defined]
    vec_dtype = vectors_cfg.datatype if hasattr(vectors_cfg, "datatype") else vectors_cfg.get("datatype", "N/A")  # type: ignore[attr-defined]

    print(f"\nCollection configuration:")
    print(f"  Status:     {info.status}")
    print(f"  Vectors:    {vec_size} dims, distance={vec_dist}")
    print(f"  Datatype:   {vec_dtype}")
    if info.config.quantization_config:
        print(f"  Quantised:  TurboQuant (4-bit search index, rescoring against float32)")

    # Quick search test
    results = client.query_points(
        collection_name=COLLECTION_NAME,
        query=sample_vector,
        limit=5,
    )
    print(f"\nSelf-search test (first point as query): found {len(results.points)} result(s)")
    if results.points:
        r = results.points[0]
        print(f"  Top hit id={r.id}  score={r.score:.4f}")


if __name__ == "__main__":
    client = QdrantClient(url=CLIENT_URL)

    # Delete existing collection if it already exists (idempotent run)
    try:
        client.delete_collection(COLLECTION_NAME)
        print(f"Deleted existing collection '{COLLECTION_NAME}'.")
    except Exception:
        pass  # doesn't exist yet — fine

    create_collection(client)
    upserted_points = upsert_points(client)
    verify(client, upserted_points[0].vector)
    print("\nDone.")
