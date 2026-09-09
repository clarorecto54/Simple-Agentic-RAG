namespace Embedding_Console;

using System.Text.Json.Nodes;

/// <summary>
/// Static helpers for batch embed operations. Visible to the test project via InternalsVisibleTo.
/// </summary>
internal static class BatchEmbedHelpers
{
    /// <summary>
    /// Extracts chunk nodes from a processed JSON root for merging into a batch output.
    /// Tries root-level 'chunks' key first, then falls back to searching any array value.
    /// </summary>
    public static List<JsonNode> ExtractChunksForMerge(JsonNode rootNode)
    {
        // Try root-level 'chunks' key first (most common case)
        if (rootNode is JsonObject obj && obj["chunks"] is JsonArray arr)
        {
            return arr.OfType<JsonNode>().ToList();
        }

        // Fallback: search any array value
        foreach (var kvp in rootNode.AsObject())
        {
            if (kvp.Value is JsonArray arr2 && arr2.Count > 0 && arr2[0] is JsonObject)
            {
                return arr2.OfType<JsonNode>().ToList();
            }
        }

        return new List<JsonNode>();
    }
}
