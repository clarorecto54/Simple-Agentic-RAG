using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Embedding_Console.Services;

/// <summary>
/// Configuration for the llama.cpp embedding service.
/// </summary>
public class LlamaCppEmbeddingOptions
{
    /// <summary>
    /// Base URL of the llama.cpp server (e.g., http://localhost:4000).
    /// </summary>
    public string ServerUrl { get; set; } = "http://localhost:4000";

    /// <summary>
    /// Model ID to use for embeddings. Can be null — llama.cpp will use the loaded model.
    /// </summary>
    public string? ModelId { get; set; } = null;

    /// <summary>
    /// Expected embedding vector dimension. If 0, dimension is inferred from the first response.
    /// </summary>
    public int ExpectedDimension { get; set; } = 0;
}

/// <summary>
/// llama.cpp embedding service using the OpenAI-compatible /v1/embeddings endpoint.
/// </summary>
public class LlamaCppEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlamaCppEmbeddingOptions _options;
    private int? _detectedDimension;

    public int EmbeddingDimension => _detectedDimension ?? throw new InvalidOperationException("No embedding has been generated yet to detect dimension.");

    public LlamaCppEmbeddingService(LlamaCppEmbeddingOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/'));
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty.", nameof(text));

        var endpoint = "/v1/embeddings";
        var requestBody = new
        {
            model = _options.ModelId ?? "",
            input = text,
            encoding_format = "float",
        };

        var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpClientException(
                $"llama.cpp returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {errorBody}",
                (int)response.StatusCode);
        }

        // Parse as JsonObject for API-compatible access (no JsonElement / TryGetValue issues)
        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataArray))
            throw new InvalidOperationException("Response missing 'data' array — invalid embedding response format.");

        if (dataArray.GetArrayLength() < 1)
            throw new InvalidOperationException("Embedding response data array is empty.");

        // Get first (and only, for single input) embedding
        var dataItem = dataArray[0];
        if (!dataItem.TryGetProperty("embedding", out var vecProp))
            throw new InvalidOperationException("Embedding response missing 'embedding' array — invalid format.");

        if (vecProp.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Embedding property is not an array — invalid format.");

        var vector = vecProp.EnumerateArray().Select(el => el.GetSingle()).ToArray();

        // Detect dimension on first successful call
        if (!_detectedDimension.HasValue)
            _detectedDimension = vector.Length;

        // Validate dimension if configured
        if (_options.ExpectedDimension > 0 && vector.Length != _options.ExpectedDimension)
        {
            throw new InvalidOperationException(
                $"Embedding dimension mismatch. Expected: {_options.ExpectedDimension}, Actual: {vector.Length}. " +
                $"The model may not match the expected collection size.");
        }

        return vector;
    }

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// Represents an HTTP error from the embedding service.
/// </summary>
public class HttpClientException : Exception
{
    public int StatusCode { get; }

    public HttpClientException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
