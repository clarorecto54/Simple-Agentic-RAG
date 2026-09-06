# Embedding Services

This document covers the service abstraction, the real HTTP client implementation for llama.cpp, and the deterministic fake service used in tests.

## IEmbeddingService (Interface)

**File:** `Services/IEmbeddingService.cs`

```csharp
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    int EmbeddingDimension { get; }
}
```

Two members:
- **GenerateEmbeddingAsync** — converts text to a `float[]` vector. Throws `ArgumentException` for null/empty text, `HttpClientException` on HTTP errors, `InvalidOperationException` on malformed responses.
- **EmbeddingDimension** — getter returning the detected dimension (throws if no embedding has been generated yet).

All public APIs have XML doc comments per project conventions.

## LlamaCppEmbeddingOptions

**File:** `Services/LlamaCppEmbeddingService.cs`

```csharp
public class LlamaCppEmbeddingOptions
{
    public string ServerUrl { get; set; } = "http://localhost:4000";
    public string? ModelId { get; set; } = null;
    public int ExpectedDimension { get; set; } = 0;
}
```

Configuration passed to the service constructor. The server URL defaults to localhost:4000 and can be overridden via `LLAMA_CPP_URL` environment variable in Program.cs.

## LlamaCppEmbeddingService (Production)

**File:** `Services/LlamaCppEmbeddingService.cs`

Implements `IEmbeddingService, IDisposable`. Uses an `HttpClient` with a 5-minute timeout to POST to llama.cpp's OpenAI-compatible endpoint.

### Request Format

```
POST /v1/embeddings
Content-Type: application/json

{
  "model": "<LLAMA_CPP_MODEL or empty string>",
  "input": "<chunk text>",
  "encoding_format": "float"
}
```

### Response Parsing

```json
{
  "data": [
    {
      "embedding": [0.123, -0.456, ...]
    }
  ]
}
```

The service:
1. Checks `"data"` array exists and has at least one element
2. Extracts the `"embedding"` field from `data[0]`
3. Converts to `float[]` via `EnumerateArray().Select(el => el.GetSingle())`
4. Detects and caches dimension on first successful call (stored in `_detectedDimension`)
5. Validates against `ExpectedDimension` if configured > 0

### Error Handling

| Scenario | Behavior |
|----------|----------|
| Text is null/empty | Throws `ArgumentException` |
| HTTP error response | Parses status code + body, throws `HttpClientException(statusCode)` |
| Missing `"data"` array | Throws `InvalidOperationException("Response missing 'data' array")` |
| Empty `"data"` array | Throws `InvalidOperationException("Embedding response data array is empty.")` |
| Missing `"embedding"` field | Throws `InvalidOperationException("Embedding response missing 'embedding' array")` |
| Dimension mismatch | Throws `InvalidOperationException("Embedding dimension mismatch...")` |

### HTTP Client Management

The service accepts an `HttpClient` from the caller (Program.cs) or creates its own default instance. `Dispose()` releases the client. Program.cs creates a single shared `HttpClient`, passes it to the service, and disposes it at program exit.

## HttpClientException

**File:** `Services/LlamaCppEmbeddingService.cs`

```csharp
public class HttpClientException : Exception
{
    public int StatusCode { get; }
}
```

Wraps HTTP errors with a readable message including status code and response body, plus the numeric `StatusCode` property for programmatic access.

## FakeEmbeddingService (Tests)

**File:** `Services/FakeEmbeddingService.cs`

Returns deterministic vectors based on SHA-256 hash of input text — no network calls needed.

### How Vectors Are Generated

1. Compute `SHA256(UTF8(text))` → 32-byte hash
2. For each dimension position `i`: take `hash[i % 32]`, normalize to [-1, 1] range via `(float)(byte / 255.0 - 0.5) * 2`
3. Cache result by input text for subsequent calls

### Features

| Feature | Detail |
|---------|--------|
| **Determinism** | Same input always produces same vector — enables reproducible tests |
| **Cross-instance determinism** | Two separate service instances produce identical vectors for the same text (Test 14) |
| **Configurable dimension** | Constructor accepts any positive integer; defaults to 1024 |
| **Caching** | Internal `Dictionary<string, float[]>` prevents redundant hashing |
| **Observability** | `ProcessedChunks` property exposes all cached embeddings for test assertions |

### Constructor & Properties

```csharp
public FakeEmbeddingService(int dimension = 1024)

public int EmbeddingDimension => _dimension;
public IReadOnlyDictionary<string, float[]> ProcessedChunks => _cache;
```

## Service Selection in Program.cs

Program.cs always uses `LlamaCppEmbeddingService` (real HTTP client). The test project constructs `FakeEmbeddingService` instances directly to run offline:

```csharp
// In Program.cs:
var embeddingService = new LlamaCppEmbeddingService(embeddingOptions, httpClient);

// In Tests/Program.cs:
var fakeSvc = new FakeEmbeddingService(1024);
```

The abstraction (`IEmbeddingService`) makes this substitution trivial — the processor depends only on the interface.

## Related Documents

- [Project Overview](01-project-overview.md) — service abstraction rationale
- [Core Components](03-core-components.md) — how the processor calls GenerateEmbeddingAsync
- [Testing](07-testing.md) — FakeEmbeddingService usage in test scenarios
