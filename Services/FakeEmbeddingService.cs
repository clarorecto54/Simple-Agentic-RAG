using System.Text;
using System.Security.Cryptography;

namespace Embedding_Console.Services;

/// <summary>
/// Deterministic fake embedding service for testing.
/// Never makes HTTP requests — returns vectors based on text hash.
/// </summary>
public class FakeEmbeddingService : IEmbeddingService
{
    private readonly int _dimension;
    private readonly Dictionary<string, float[]> _cache = new();

    /// <summary>
    /// Creates a fake embedding service with the specified vector dimension.
    /// </summary>
    /// <param name="dimension">The dimension of generated vectors (default: 1024).</param>
    public FakeEmbeddingService(int dimension = 1024)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension), "Dimension must be positive.");

        _dimension = dimension;
    }

    /// <summary>
    /// Gets all chunks that have been processed. Useful for test assertions.
    /// </summary>
    public IReadOnlyDictionary<string, float[]> ProcessedChunks => _cache;

    public int EmbeddingDimension => _dimension;

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty.", nameof(text));

        // Check cancellation before doing work
        cancellationToken.ThrowIfCancellationRequested();

        // Return cached result if same text was processed before
        if (_cache.TryGetValue(text, out var cached))
            return cached;

        // Generate deterministic vector from text hash
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));

        var vector = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
        {
            // Use hash bytes deterministically, cycling through them
            var byteIndex = i % hash.Length;
            vector[i] = ((float)hash[byteIndex] / byte.MaxValue - 0.5f) * 2;
        }

        _cache[text] = vector;
        return await Task.FromResult(vector);
    }
}
