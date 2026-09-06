namespace Embedding_Console.Services;

/// <summary>
/// Interface for text-to-vector embedding generation.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generate an embedding vector from the provided text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector as a float array.</returns>
    /// <exception cref="ArgumentException">Thrown when text is null or empty.</exception>
    /// <exception cref="HttpClientException">Thrown on HTTP errors from the embedding service.</exception>
    /// <exception cref="InvalidOperationException">Thrown on invalid response format.</exception>
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the embedding dimension from the service (e.g. llama.cpp model's n_embd).
    /// </summary>
    int EmbeddingDimension { get; }
}
