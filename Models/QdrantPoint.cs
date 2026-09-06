using System.Text.Json.Nodes;

namespace Embedding_Console.Models;

/// <summary>
/// Represents a Qdrant-compatible point with id, vector, and payload.
/// Suitable for direct use in Qdrant's batch upsert API.
/// </summary>
public record QdrantPoint(
    string Id,
    object Vector,
    JsonNode Payload);
