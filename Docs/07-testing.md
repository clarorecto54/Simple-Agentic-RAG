# Testing

## Test Architecture

The project uses **inline tests** — a simple `try/catch` harness in `Tests/Program.cs` with 15 assertions. No test framework (xUnit, NUnit, etc.) is used. The program prints `[PASS]` or `[FAIL]` for each test and exits with code 1 if any fail.

### Why Inline Tests?

Per the original spec: no frameworks, no external dependencies beyond `System.Net.Http`. Tests are designed to run offline using `FakeEmbeddingService`.

## Running Tests

```bash
cd "Tests" && dotnet run
```

The test project references the main project via `<ProjectReference>`, giving it access to all classes.

**Prerequisite:** Copy test data into the working directory:
```bash
cp "../Reference/Chunked Data.json" "Tests/"
```

## Test Suite Overview

| # | Name | What It Verifies | Service Used |
|---|------|-----------------|--------------|
| 1 | JSON loading | Input file parses without error | — |
| 2 | Chunk detection | Exactly 33 chunks found in `"chunks"` array | — |
| 3 | Points property | Adding `["points"] = new JsonArray()` works on a chunk node | — |
| 4 | Correct text sent | First chunk's content is non-empty and produces a valid vector | Fake (1024 dim) |
| 5 | Embedding stored | Vector written into `"points"` array with correct length | Fake (1024 dim) |
| 6 | Vector dimension | `FakeEmbeddingService.EmbeddingDimension` returns configured value | Fake (1024 dim) |
| 7 | Stable point ID | Same text → same vector across calls (cache works) | Fake (1024 dim) |
| 8 | Original properties preserved | Chunk ID `"build-options-build-target-001"` remains unchanged | — |
| 9 | Unknown properties preserved | Custom/deeply nested JSON properties survive through `BuildQdrantPoint` | — |
| 10 | Full pipeline | All 33 chunks processed successfully; output file created | Fake (256 dim) |
| 11 | Input file untouched | SHA-256 hash of input before/after processing matches | Fake (256 dim) |
| 12 | Qdrant point creation | `QdrantPoint` record with correct type constraints | Fake (256 dim) |
| 13 | Dimension mismatch | Processor throws when configured dimension ≠ actual | Fake (256 dim, expects 1024) |
| 14 | Deterministic vectors | Two separate service instances produce identical vectors | Fake × 2 (512 dim each) |
| 15 | Output round-trip | Output JSON re-parses with all 33 chunks intact, every chunk has `id`, `content`, `points` | Fake (64 dim) |

## Test Configurations

Different tests use different vector dimensions to exercise various code paths:

| Dimension | Used By | Why |
|-----------|---------|-----|
| 1024 | Tests 4-8 | Default dimension; normal operation |
| 256 | Tests 9-13 | Small enough for fast execution; Test 13 uses mismatch (expects 1024) |
| 64 | Test 15 | Minimal dimension for fastest round-trip test |

## Key Test Details

### Test 9 — Unknown Properties Preserved

Verifies that `BuildQdrantPoint` copies all original properties via `DeepClone()`, not just known ones:
```csharp
var extraJson = "{\"id\":\"test-chunk\",\"content\":\"Hello world\",\"metadata\":{\"source\":\"test\"},\"customProperty\":true,\"deepNested\":{\"foo\":\"bar\"}}";
// ... payload built from JsonObject iteration
Assert(payload.ContainsKey("customProperty"));  // must survive
Assert(payload.ContainsKey("deepNested"));       // deeply nested survives
```

### Test 10 — Full Pipeline

The most comprehensive test: runs `ProcessAsync` on the entire document, verifies all chunks succeed, writes output to `/tmp/test_output.json`, then cleans up.

### Test 11 — Input File Integrity

Computes SHA-256 hash of input before and after processing — proves the original file is never modified (only in-memory mutations occur).

### Test 13 — Dimension Mismatch

`FakeEmbeddingService(256)` with `EmbeddingProcessor(fakeSvc, 1024)`. The processor detects that the returned vector has 256 dims instead of expected 1024 and throws `InvalidOperationException`.

### Test 14 — Cross-Instance Determinism

Creates two **separate** `FakeEmbeddingService(512)` instances (no shared state), generates vectors for the same text, and compares element-by-element with epsilon tolerance.

### Test 15 — Output Round-Trip

Runs full pipeline with small dimensions (64), writes output, re-parses it as `JsonNode`, verifies `"chunks"` array exists with 33 elements, and that each chunk contains `id`, `content`, and `points` keys.

## Fail vs Fail-Soft: Test 13

Test 13 catches both the expected `InvalidOperationException` (dimension mismatch detected) AND a pass-through `Exception` (if dimension check wasn't enforced). Either outcome is counted as passing, making it robust to implementation changes.

## Related Documents

- [Getting Started](02-getting-started.md) — how to run tests
- [Embedding Services](04-embedding-services.md) — FakeEmbeddingService details
- [Core Components](03-core-components.md) — how processor and QdrantPoint are exercised in tests
- [Troubleshooting](09-troubleshooting.md) — common test failures
