"""Manage a Qdrant collection: create (with llama.cpp model verification) or upsert points."""

import argparse
import json
import sys
from pathlib import Path

from qdrant_client import QdrantClient, models


# ---------------------------------------------------------------------------
# Helpers — accept client directly; no module-global mutation needed.
# ---------------------------------------------------------------------------

DEFAULT_COLLECTION = "Stock Knowledge"


def fetch_llama_model_info(llama_url: str) -> dict:
    """Query llama.cpp /v1/models to discover the loaded embedding model and its dimension.

    llama.cpp exposes an OpenAI-compatible /v1/models endpoint.
    Returns a dict with at least {"id": "<model-id>", "embedding_dim": <int>}.
    Raises on HTTP error or missing data.
    """
    import requests

    url = f"{llama_url.rstrip('/')}/v1/models"
    resp = requests.get(url, timeout=15)
    resp.raise_for_status()

    body = resp.json()
    # OpenAI-compatible response: {"data": [{"id": "...", ...}, ...]}
    items = body.get("data", [])
    if not items:
        raise ValueError(f"No models returned from {url}")

    # Prefer the first model; collect its properties.
    model = items[0]
    model_id = model.get("id", "unknown")

    # llama.cpp embeds dimension in a field called "n_embd" on the model object.
    dim = model.get("embedding_dim") or model.get("n_embd")
    if dim is None:
        raise ValueError(
            f"Model '{model_id}' returned by llama.cpp does not include an "
            f"'embedding_dim' or 'n_embd' field. "
            "Use --dimension to set it manually."
        )

    return {"id": model_id, "embedding_dim": int(dim)}


def create_collection(
    client: QdrantClient, collection_name: str, vector_size: int, vector_name: str | None = None
):
    """Create the collection with maximum-performance settings for a small dataset.

    The default config (float32 vectors + TurboQuant BITS4 rescoring) is appropriate
    when *vector_size* <= 4096 and point count stays under ~10k.

    Rationale per parameter:
    - datatype=float32 : full precision avoids quantisation loss; tiny dataset, RAM is cheap.
    - distance=COSINE  : standard for cosine-normalised embeddings (Qwen3-Embedding et al.).
    - default_segment_number=1 : single segment — no merge overhead at query time.
    - max_segment_size=50_000 : far exceeds expected cardinality; avoids unwanted splits.
    - indexing_threshold=5_000 : index already ready for this size (no lazy rebuild).
    - HNSW m=6, ef_construct=128 : compact graph tuned for small cardinalities.
    - TurboQuant BITS4 : 4-bit search index; original float32 kept on disk for rescoring.
      Recall within ~1-2 pp of raw float32 at ~4x throughput.
    """

    if vector_name:
        # Named vector mode — create collection with explicit named vector schema.
        vectors_config = {
            vector_name: models.VectorParams(
                size=vector_size,
                distance=models.Distance.COSINE,
                datatype=models.Datatype.FLOAT32,
                memory=models.Memory.CACHED,
            ),
        }
    else:
        # Legacy single-vector mode — no named vectors.
        vectors_config = models.VectorParams(
            size=vector_size,
            distance=models.Distance.COSINE,
            datatype=models.Datatype.FLOAT32,
            memory=models.Memory.CACHED,
        )

    optimizers_config = models.OptimizersConfigDiff(
        default_segment_number=1,
        max_segment_size=50_000,           # far exceeds expected point count
        indexing_threshold=5_000,          # index always ready (no lazy rebuild)
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
        collection_name=collection_name,
        vectors_config=vectors_config,
        optimizers_config=optimizers_config,
        hnsw_config=hnsw_config,
        quantization_config=quantization_config,
    )
    print(f"Collection '{collection_name}' created successfully (dim={vector_size}).")


def upsert_points(client: QdrantClient, json_path: str, collection_name: str) -> int:
    """Load points from a JSON file (output of the C# pipeline) and upsert them.

    Expected schema matches `output.json`: {"chunks": [{"id", "content", "metadata", "points": [...]}]}.
    Each point entry needs an "id" and "vector".
    Vectors are sent as named vectors using the collection's vector name.
    """

    # Read the collection config to discover the named-vector key.
    try:
        info = client.get_collection(collection_name)
        vec_cfg = info.config.params.vectors  # type: ignore[union-attr]
        if isinstance(vec_cfg, dict):
            vector_name = next(iter(vec_cfg), None)
            if not vector_name:
                sys.exit(f"Cannot determine named-vector key from collection config.")
        else:
            vector_name = ""  # legacy single-vector config — use raw array below
    except Exception as exc:
        sys.exit(f"Failed to read collection config: {exc}")

    path = Path(json_path)
    if not path.exists():
        sys.exit(f"File not found: {json_path}")

    with open(path, "r") as f:
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

            # Format the vector for the upsert call.
            if isinstance(vector, dict) and vector_name:
                # Named-vector format: {"jina-embeddings-v3": [...]}
                upsert_vector = {vector_name: vector[vector_name]}
            elif isinstance(vector, list):
                # Legacy raw array — only for single-vector collections.
                upsert_vector = vector
            else:
                continue

            all_points.append(
                models.PointStruct(
                    id=next_id,
                    vector=upsert_vector,
                    payload=payload | {"point_string_id": point_str_id},
                )
            )
            next_id += 1

    if not all_points:
        sys.exit(f"No valid points found in {json_path} (expected 'chunks[].points[]').")

    # Upsert in batches of 100.
    batch_size = 100
    for i in range(0, len(all_points), batch_size):
        batch = all_points[i : i + batch_size]
        client.upsert(collection_name=collection_name, points=batch)

    return len(all_points)


def verify_config(client: QdrantClient, collection_name: str, expected_dim: int):
    """Verify the collection exists and report its status."""
    try:
        info = client.get_collection(collection_name)
    except Exception as exc:
        sys.exit(f"Collection '{collection_name}' not found: {exc}")

    count = client.count(collection_name)

    vectors_cfg = info.config.params.vectors  # type: ignore[union-attr]
    if hasattr(vectors_cfg, "size"):
        # Single-vector config (VectorParams)
        vec_size = vectors_cfg.size  # type: ignore[attr-defined]
        vec_dist = vectors_cfg.distance  # type: ignore[attr-defined]
    elif isinstance(vectors_cfg, dict):
        # Multi-vector config — use first key's params
        first_key = next(iter(vectors_cfg), "N/A")
        cfg = vectors_cfg.get(first_key) if first_key != "N/A" else None
        vec_size = cfg.size if cfg else "N/A"  # type: ignore[union-attr]
        vec_dist = cfg.distance if cfg else "N/A"  # type: ignore[attr-defined]
    else:
        vec_size = "N/A"
        vec_dist = "N/A"

    print(f"\nCollection configuration:")
    print(f"  Status:     {info.status}")
    print(f"  Points:     {count.count}")
    print(f"  Vectors:    {vec_size} dims, distance={vec_dist}")

    if vec_size != expected_dim:
        print(
            f"  ⚠ WARNING: Expected dimension {expected_dim}, "
            f"but collection reports {vec_size}. Search results may be wrong."
        )

    if info.config.quantization_config:
        print(f"  Quantised:  TurboQuant (4-bit search index, rescoring against float32)")


# ---------------------------------------------------------------------------
# Subcommand implementations — each receives a QdrantClient directly.
# ---------------------------------------------------------------------------

def cmd_create(args: argparse.Namespace, qdrant_url: str) -> None:
    """Create subcommand: verify llama.cpp model → create collection."""

    client = QdrantClient(url=qdrant_url)
    collection_name = args.collection or DEFAULT_COLLECTION

    # Auto-detect via llama.cpp or require --dimension.
    if not args.dimension:
        if not args.llama_url:
            sys.exit(
                "--dimension is required when --llama-url is not provided. "
                "(Or pass --llama-url to auto-detect from the model.)"
            )
        print(f"Querying llama.cpp at {args.llama_url} for model info …")
        try:
            model_info = fetch_llama_model_info(args.llama_url)
        except Exception as exc:
            sys.exit(f"Failed to query llama.cpp: {exc}")
        verified_dim = model_info["embedding_dim"]
        print(f"  Model : {model_info['id']}")
        print(f"  Dimension (verified): {verified_dim}")
    else:
        verified_dim = args.dimension
        print(f"Dimension (supplied): {verified_dim}")

    # 2. Delete existing collection if present (idempotent).
    try:
        client.delete_collection(collection_name)
        print(f"Deleted existing collection '{collection_name}'.")
    except Exception:
        pass  # doesn't exist yet — fine

    create_collection(client, collection_name, verified_dim, args.vector_name)

    # 3. Verify the created collection.
    verify_config(client, collection_name, verified_dim)
    print("\nDone.")


def cmd_upsert(args: argparse.Namespace, qdrant_url: str) -> None:
    """Upsert subcommand: load points from JSON and upsert into the collection."""

    client = QdrantClient(url=qdrant_url)
    collection_name = args.collection or DEFAULT_COLLECTION
    json_path = args.json_path

    # Verify the target collection exists first.
    try:
        client.get_collection(collection_name)
    except Exception:
        sys.exit(f"Collection '{collection_name}' does not exist. Run 'create' first.")

    count = upsert_points(client, json_path, collection_name)
    print(f"Upserted {count} points into '{collection_name}'.")

    # Quick sanity check — report back config without dimension mismatch warning.
    verify_config(client, collection_name, 0)
    print("\nDone.")


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Manage a Qdrant embedding collection: create (with llama.cpp verification) or upsert points."
    )
    parser.add_argument(
        "--collection", "-c",
        default=DEFAULT_COLLECTION,
        help=f"Name of the Qdrant collection (default: '{DEFAULT_COLLECTION}').",
    )
    parser.add_argument(
        "--qdrant-url",
        default="http://localhost:6333",
        help="Qdrant server URL (default: http://localhost:6333).",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    # ---- create ----
    create_parser = subparsers.add_parser(
        "create",
        help="Create collection after verifying llama.cpp model dimension.",
    )
    create_parser.add_argument(
        "--llama-url", "-l",
        default=None,
        help="llama.cpp server URL (default: http://localhost:4000). Used to query model info.",
    )
    create_parser.add_argument(
        "--dimension", "-d",
        type=int,
        default=0,
        help=(
            "Force the vector dimension. When --llama-url is given, this must match "
            "the reported dimension (or be omitted for auto-detection)."
        ),
    )
    create_parser.add_argument(
        "--vector-name",
        default=None,
        help=(
            "Named vector name for the collection. If omitted, creates a legacy "
            "single-vector collection with no named vectors (Qdrant will use empty-string key). "
            "Example: --vector-name qwen-embeddings"
        ),
    )

    # ---- upsert ----
    upsert_parser = subparsers.add_parser(
        "upsert",
        help="Upsert points from a JSON file (output of the C# pipeline).",
    )
    upsert_parser.add_argument(
        "--json-path", "-j",
        required=True,
        help="Path to the JSON file containing embedded chunks (e.g. ./output.json).",
    )

    args = parser.parse_args()

    if args.command == "create":
        cmd_create(args, args.qdrant_url)
    elif args.command == "upsert":
        cmd_upsert(args, args.qdrant_url)


if __name__ == "__main__":
    main()
