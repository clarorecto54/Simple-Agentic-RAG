# Task: Implement JSON ETL → llama.cpp Embedding → Qdrant-Ready Points

You are working inside an existing **C# console application**.

Modify the existing project to implement the following pipeline:

```text
JSON File
    ↓
ETL / Parse
    ↓
Add "points" property
    ↓
Extract content to embed
    ↓
HTTP request to llama.cpp embedding server
    ↓
Receive embedding vector
    ↓
Store embedding result in "points"
    ↓
Output processed JSON file
```

The final JSON file must contain the **original payload completely intact**, with the only addition being the `"points"` property containing the embedding/Qdrant point data.

---

# CRITICAL REQUIREMENT — DO NOT ALTER THE ORIGINAL PAYLOAD

This is the highest-priority requirement.

The input JSON represents already-processed/chunked data.

**Do not redesign or restructure this payload.**

Do not:

- Rename existing properties
- Remove existing properties
- Move existing properties
- Change existing values
- Change existing data types
- Flatten nested structures
- Reorganize arrays
- Discard unknown properties
- Normalize the JSON
- Replace the original objects with a different DTO representation
- Modify the original input file

The only modification to the existing payload should be the addition of:

```json
"points": [...]
```

The embedding result generated from llama.cpp must be stored inside this new property.

---

# 1. Inspect the Existing Project First

Before making changes:

1. Inspect the complete C# project.
2. Understand the current project architecture.
3. Identify:
   - `Program.cs`
   - Existing models
   - Existing ETL logic
   - Existing JSON handling
   - Existing services
   - Existing HTTP clients
   - Existing test project
   - Existing NuGet packages
   - .NET version
4. Inspect the supplied JSON payload.
5. Determine exactly where the chunks are located.
6. Determine exactly which property contains the text/content that should be embedded.

Do not assume the payload structure.

Use the actual supplied JSON schema.

Preserve the project's existing architecture and coding conventions where possible.

---

# 2. Command-Line Input

The console application must accept a JSON file path through command-line arguments.

Example:

```bash
dotnet run -- "C:\data\chunks.json"
```

Also support an optional output path:

```bash
dotnet run -- "C:\data\chunks.json" "C:\data\embedded.json"
```

If an output path is not provided, automatically generate one based on the input filename.

For example:

```text
chunks.json
```

becomes:

```text
chunks.embedded.json
```

The application must:

1. Validate that an input path was supplied.
2. Validate that the file exists.
3. Read the file.
4. Parse the JSON.
5. Perform the ETL.
6. Add the `points` property.
7. Generate embeddings.
8. Populate `points`.
9. Write the resulting JSON to the output file.
10. Never overwrite the original input file by default.

---

# 3. ETL Stage

The application should have a clear ETL/processing stage.

Conceptually:

```text
Input JSON
    ↓
Extract chunks
    ↓
Prepare embedding input
    ↓
Send content to embedding service
    ↓
Receive embedding
    ↓
Attach embedding/Qdrant point to original chunk
    ↓
Write output JSON
```

The ETL must preserve the original data.

The ETL should identify each chunk and extract only the text necessary for embedding.

Determine the actual content property from the supplied payload.

Do not assume it is called `content`.

---

# 4. Add the `points` Property

Once the chunks have been identified, add:

```json
"points": []
```

to each appropriate chunk.

Then populate that property after the embedding request succeeds.

Conceptually:

```text
Original chunk
      ↓
Add "points": []
      ↓
Extract chunk text
      ↓
HTTP → llama.cpp
      ↓
Receive vector
      ↓
Create Qdrant point
      ↓
points = [Qdrant point]
```

The `points` property must belong to the same original chunk object.

---

# 5. llama.cpp Embedding HTTP Request

The production application must send the chunk's text to the llama.cpp embedding server.

Current server:

```text
http://localhost:4000
```

The actual embedding endpoint and request/response format should be determined from the llama.cpp API supported by the project's version.

Do not hard-code the URL throughout the application.

Use configuration where appropriate.

The flow should be:

```text
Chunk text
    ↓
HTTP POST
    ↓
llama.cpp embedding endpoint
    ↓
JSON response
    ↓
Extract embedding vector
```

Use asynchronous HTTP APIs.

---

# 6. Embedding Service Abstraction

Create an abstraction such as:

```csharp
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}
```

The exact implementation can follow the project's existing architecture.

Create a llama.cpp implementation:

```text
IEmbeddingService
       ↓
LlamaCppEmbeddingService
       ↓
HTTP → llama.cpp
```

The ETL processor should depend on `IEmbeddingService`, not directly on HTTP.

---

# 7. Store the Embedding in `points`

When llama.cpp returns the embedding vector, create a Qdrant-compatible point and store it inside the newly added `points` property.

Conceptually:

```json
{
    "id": "chunk-001",
    "content": "Some text",
    "metadata": {
        "source": "example.md"
    },
    "points": [
        {
            "id": "chunk-001",
            "vector": [
                0.123,
                -0.456,
                0.789
            ],
            "payload": {
                "id": "chunk-001",
                "content": "Some text",
                "metadata": {
                    "source": "example.md"
                }
            }
        }
    ]
}
```

This is only an example.

**Use the actual input schema.**

Do not modify the existing properties to match the example.

---

# 8. Qdrant Point Structure

The objects inside `points` must be suitable for Qdrant's point upsert API.

A point should contain the appropriate:

```text
id
vector
payload
```

The `vector` must be the actual vector returned by llama.cpp.

Do not fabricate, truncate, pad, or otherwise modify the embedding unless explicitly required.

---

# 9. Stable Point IDs

Each Qdrant point must have a stable ID.

If the original chunk already has a suitable unique identifier, use it.

Do not generate a random ID for every execution.

Processing the same chunk multiple times should result in the same logical point ID.

This is important because the points will eventually be upserted into Qdrant.

---

# 10. Qdrant Payload

The Qdrant point payload should contain the information needed to retrieve the original chunk after a vector search.

Determine the appropriate fields from the actual payload.

Potential information includes:

- Chunk ID
- Document ID
- Source
- File path
- Chunk index
- Content
- Metadata

However, **do not move these properties out of the original chunk**.

They are copied into the Qdrant point's payload only for retrieval purposes.

The original object remains unchanged.

---

# 11. Preserve Unknown JSON Properties

The input JSON may contain properties that are not represented by the current C# models.

Those properties must survive.

For example:

```json
{
    "existingProperty": "value",
    "unknownProperty": {
        "foo": "bar"
    }
}
```

must remain:

```json
{
    "existingProperty": "value",
    "unknownProperty": {
        "foo": "bar"
    },
    "points": [...]
}
```

Consider using:

```csharp
JsonNode
```

or:

```csharp
JsonObject
```

for the final JSON mutation.

The goal is to modify the existing JSON structure rather than reconstructing it and accidentally losing fields.

---

# 12. Output JSON File

After all embeddings have been generated, write the complete processed payload to a new JSON file.

Example:

```bash
dotnet run -- ./data/chunks.json ./output/embedded.json
```

The output must contain:

```text
Original JSON
+
points properties
+
Qdrant point objects
+
embedding vectors
```

The original input file must remain unchanged.

---

# 13. Output Structure

If the input is conceptually:

```json
{
    "chunks": [
        {
            "id": "chunk-001",
            "content": "Hello world",
            "metadata": {
                "source": "example.md"
            }
        }
    ]
}
```

the output should conceptually be:

```json
{
    "chunks": [
        {
            "id": "chunk-001",
            "content": "Hello world",
            "metadata": {
                "source": "example.md"
            },
            "points": [
                {
                    "id": "chunk-001",
                    "vector": [
                        0.123,
                        -0.456,
                        0.789
                    ],
                    "payload": {
                        "id": "chunk-001",
                        "content": "Hello world",
                        "metadata": {
                            "source": "example.md"
                        }
                    }
                }
            ]
        }
    ]
}
```

Again, this is only an illustration.

**Do not change the real payload structure to match this example.**

---

# 14. Vector Dimension

Validate the embedding dimension.

For example, if the Qdrant collection expects:

```text
1024
```

then every generated vector must contain exactly 1024 values.

If llama.cpp returns a different dimension, fail clearly.

Example:

```text
Embedding dimension mismatch.
Expected: 1024
Actual: 768
Chunk: chunk-001
```

Do not silently truncate or pad vectors.

Make the expected dimension configurable.

---

# 15. Processing Strategy

Process chunks in a controlled manner.

The initial implementation can process:

```text
chunk 1 → llama.cpp → vector
chunk 2 → llama.cpp → vector
chunk 3 → llama.cpp → vector
...
```

Do not introduce uncontrolled parallel requests that could overwhelm llama.cpp.

If batching is supported by the embedding API, structure the implementation so batching can be added later.

---

# 16. Progress Reporting

Display useful progress information.

For example:

```text
Input:
  ./data/chunks.json

Chunks:
  1056

Generating embeddings...

Processed: 1056 / 1056

Points generated:
  1056

Embedding dimension:
  1024

Output:
  ./data/chunks.embedded.json

Completed successfully.
```

Do not print complete embedding vectors to the console.

---

# 17. Error Handling

Handle errors clearly.

At minimum:

- Missing input argument
- Missing input file
- Invalid JSON
- Missing chunk content
- HTTP connection failure
- HTTP error response
- Invalid llama.cpp response
- Missing embedding vector
- Invalid vector dimension
- Invalid/missing point ID
- File write failure

When possible, identify the chunk associated with the failure.

Example:

```text
Failed to generate embedding for chunk: chunk-001
```

Do not silently ignore failed embeddings.

---

# 18. IMPORTANT — Use `brave-search` MCP for Troubleshooting

You have access to a `brave-search` MCP tool.

Use it **when you encounter an error or an unfamiliar technical issue that cannot be confidently resolved from the existing code/project context**.

The purpose is to reduce blind retry loops.

Follow this troubleshooting strategy:

```text
Encounter error
      ↓
Inspect error message
      ↓
Inspect relevant local code/configuration
      ↓
Determine likely cause
      ↓
Can it be confidently fixed locally?
      ├── YES → Fix → Test
      │
      └── NO
           ↓
      Search using brave-search MCP
           ↓
      Find authoritative/relevant documentation
           ↓
      Apply the appropriate fix
           ↓
      Test again
```

## Do NOT blindly retry

Do not repeatedly make the same change and rerun the project if the same error is occurring.

If the same error occurs twice without a meaningful change in diagnosis or implementation:

1. Stop repeating the same attempt.
2. Analyze the error.
3. Use `brave-search` MCP to research the issue if external documentation could help.
4. Apply a specific evidence-based fix.
5. Retry.

---

# 19. When to Use `brave-search`

Use `brave-search` MCP particularly for issues involving:

- llama.cpp API behavior
- llama.cpp embedding endpoint/request format
- llama.cpp response format
- llama.cpp command-line/API documentation
- Qdrant point/vector API format
- .NET API behavior that is uncertain
- NuGet package behavior
- C# library/API changes
- JSON serialization behavior
- HTTP client behavior
- Version-specific framework issues
- Unexpected errors where the local code does not explain the behavior
- Documentation that may have changed between versions

Prefer official documentation or primary sources when available.

For example, if the llama.cpp API behavior is unclear, search for the relevant llama.cpp documentation rather than guessing the request format.

If Qdrant's API structure is unclear, search Qdrant's official documentation.

---

# 20. Search Before Making Version-Sensitive Assumptions

If an error appears related to a particular version of:

- .NET
- C#
- llama.cpp
- Qdrant
- NuGet packages
- HTTP libraries

do not assume the behavior from memory.

Use `brave-search` MCP to verify the current/version-specific behavior.

Pay attention to the versions actually installed in this project.

Do not blindly apply instructions intended for a different version.

---

# 21. Prefer Root-Cause Fixes

When troubleshooting:

Bad approach:

```text
Error
 ↓
Random change
 ↓
Retry
 ↓
Same error
 ↓
Another random change
 ↓
Retry
```

Preferred approach:

```text
Error
 ↓
Read error carefully
 ↓
Inspect code/config
 ↓
Identify likely root cause
 ↓
Search documentation if necessary
 ↓
Make targeted change
 ↓
Test
```

Keep retry attempts purposeful.

---

# 22. Unit Tests — DO NOT USE THE REAL llama.cpp Server

The production application must use the real llama.cpp HTTP endpoint.

However, automated tests must **not call**:

```text
http://localhost:4000
```

The current server is running an instruction model rather than an embedding model.

Use a mocked/fake embedding service for ETL tests.

```text
IEmbeddingService
        ↓
Fake/Mock Embedding Service
        ↓
Deterministic vector
```

No real network calls should be required for the tests.

---

# 23. Fake Embedding Service

Create a deterministic fake implementation.

It should:

- Never make HTTP requests.
- Return deterministic vectors.
- Return the same vector for identical text.
- Support configurable dimensions.
- Allow tests to verify which chunks were processed.
- Allow tests to simulate failures.

For example:

```text
Input:
  "Hello world"

Output:
  deterministic 1024-dimensional vector
```

The exact values are not important.

Determinism is important.

---

# 24. Test the Real HTTP Client Without llama.cpp

If practical, test `LlamaCppEmbeddingService` using a fake/mock `HttpMessageHandler`.

Conceptually:

```text
LlamaCppEmbeddingService
        ↓
HttpClient
        ↓
Fake HttpMessageHandler
        ↓
Simulated llama.cpp response
```

This allows testing:

- HTTP request construction
- Request JSON
- Endpoint construction
- Response parsing
- Vector extraction
- HTTP error handling
- Invalid response handling

without requiring a running llama.cpp server.

---

# 25. Unit Tests

Create tests covering:

### Test 1 — JSON loading

Verify that the input JSON is loaded correctly.

### Test 2 — Chunk detection

Verify that the expected chunks are found.

### Test 3 — `points` property

Verify that the property is added to the appropriate chunks.

### Test 4 — Correct text sent to embedding service

Verify that the exact expected chunk content is passed to the embedding service.

### Test 5 — Embedding stored

Verify that the returned vector is stored inside the Qdrant point in `points`.

### Test 6 — Vector dimension

Verify the expected dimension.

### Test 7 — Stable point ID

Verify that the same chunk produces the same point ID.

### Test 8 — Original properties preserved

Verify that existing properties and values remain unchanged.

### Test 9 — Unknown properties preserved

Verify that properties not represented by C# models remain intact.

### Test 10 — Output file

Verify that the processed JSON is written successfully.

### Test 11 — Input file untouched

Verify that the original input file is not modified.

### Test 12 — Invalid file

Verify graceful handling of a nonexistent file.

### Test 13 — Invalid JSON

Verify malformed JSON handling.

### Test 14 — Embedding failure

Simulate an embedding failure and verify appropriate handling.

### Test 15 — Dimension mismatch

Simulate an incorrect vector dimension and verify appropriate handling.

---

# 26. Output Must Be Suitable for Qdrant

The resulting JSON must make it straightforward to extract:

```text
points[*]
```

from each chunk and eventually combine them into a Qdrant batch.

Conceptually:

```text
embedded.json
      ↓
chunks
      ↓
chunks[*].points[*]
      ↓
flatten
      ↓
Qdrant points batch
      ↓
Qdrant upsert
```

Do not implement the actual Qdrant HTTP request unless specifically requested.

The current task is to generate the Qdrant-ready point data.

---

# 27. Architecture

Prefer an architecture similar to:

```text
                    ┌───────────────────┐
                    │    Program.cs     │
                    │   CLI Arguments   │
                    └─────────┬─────────┘
                              │
                              ▼
                    ┌───────────────────┐
                    │   JSON Loader     │
                    └─────────┬─────────┘
                              │
                              ▼
                    ┌───────────────────┐
                    │   ETL Processor   │
                    └─────────┬─────────┘
                              │
                       Extract text
                              │
                              ▼
                    ┌───────────────────┐
                    │ IEmbeddingService │
                    └─────────┬─────────┘
                              │
                              ▼
                    ┌───────────────────┐
                    │ llama.cpp Client  │
                    └─────────┬─────────┘
                              │
                              ▼
                       Embedding vector
                              │
                              ▼
                    ┌───────────────────┐
                    │ Point Builder     │
                    └─────────┬─────────┘
                              │
                              ▼
                       points property
                              │
                              ▼
                    ┌───────────────────┐
                    │   JSON Writer     │
                    └─────────┬─────────┘
                              │
                              ▼
                    embedded.json
```

Keep the embedding implementation separate from JSON processing.

---

# 28. Do Not Overengineer

The project is a C# console application.

Do not introduce unnecessary:

- frameworks
- databases
- background workers
- message queues
- complicated dependency injection
- unnecessary design patterns

Use simple, maintainable components appropriate for the existing project.

Reuse existing dependencies when possible.

---

# 29. Final Validation

After implementation:

1. Build the project.
2. Run all unit tests.
3. Confirm all tests pass.
4. Confirm tests do not contact `localhost:4000`.
5. Run the console application against the supplied JSON.
6. Confirm the application makes the llama.cpp embedding HTTP requests.
7. Confirm embeddings are successfully received.
8. Confirm embeddings are stored in `points`.
9. Confirm the output JSON is generated.
10. Compare input and output.
11. Confirm existing data remains intact.
12. Confirm only `points` was added.
13. Confirm vector dimensions are correct.
14. Confirm point IDs are stable.
15. Confirm the point structure is suitable for Qdrant.
16. Confirm the original input file was not modified.

If an error occurs during this process:

- First inspect and diagnose it.
- Do not blindly repeat the same attempt.
- If the cause is unclear or version/API documentation is required, use the `brave-search` MCP.
- Prefer authoritative documentation.
- Apply a targeted fix.
- Retry only after making a meaningful change.

---

# 30. Deliverables

Provide:

1. Modified C# source code.
2. CLI argument handling.
3. JSON ETL/processing logic.
4. `points` property generation.
5. llama.cpp embedding HTTP client.
6. `IEmbeddingService`.
7. Fake/mock embedding service.
8. Qdrant point representation.
9. JSON output functionality.
10. Unit tests.
11. Error handling.
12. Example CLI commands.
13. Example output structure.
14. Short architecture explanation.

At the end, report:

### Files Changed

List modified and newly created files.

### Pipeline

```text
JSON → ETL → add points → llama.cpp → embedding → points → output JSON
```

### CLI Usage

Show the exact command to process a JSON file.

### Output

Show the generated output file path.

### llama.cpp

Explain how the embedding HTTP client works.

### Qdrant

Explain how the generated `points` can eventually be extracted and sent to Qdrant.

### Tests

Confirm that automated tests use mocks/fakes and do not contact the real llama.cpp server.

### Troubleshooting

If any implementation issue occurred, briefly state:

- What the error was
- What caused it
- Whether `brave-search` MCP was used
- What fix was applied
- Whether the final tests passed

---

# FINAL AND HIGHEST-PRIORITY RULE

The application must implement:

```text
JSON
 ↓
ETL
 ↓
ADD "points"
 ↓
SEND CHUNK CONTENT TO LLAMA.CPP
 ↓
RECEIVE EMBEDDING
 ↓
STORE EMBEDDING IN "points"
 ↓
OUTPUT JSON
```

The original payload must remain intact.

The **only addition to the original chunk objects is the `points` property**.

Do not redesign the payload.

Do not replace the payload with Qdrant points.

Do not remove or rename existing fields.

Do not modify existing values.

Do not discard unknown properties.

Use `brave-search` MCP when an unfamiliar error, API behavior, or version-specific issue requires external documentation, rather than repeatedly retrying the same unsuccessful approach.