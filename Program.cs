using Embedding_Console;
using Embedding_Console.Processors;
using Embedding_Console.Services;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;

// ─────────────── CLI Argument Parsing ───────────────
if (args.Length < 1)
{
    HelpText.PrintMain();
    return 1;
}

string command = args[0];

if (command == "--help" || command == "-h")
{
    HelpText.PrintMain();
    return 0;
}

switch (command)
{
    case "embed":
        return await RunEmbedAsync(args.Skip(1).ToArray());
    case "rag":
        return await RunRagAsync(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"Error: Unknown command '{command}'.");
        Console.WriteLine();
        Console.WriteLine("Available commands: embed, rag");
        Console.WriteLine();
        Console.WriteLine("Use 'dotnet run -- --help' for usage.");
        return 1;
}

// ─────────────── Embed Command ───────────────
static async Task<int> RunEmbedAsync(string[] subArgs)
{
    // ── Embed subcommand argument parsing ──────────────
    string? inputDir = null;
    List<string> inputFiles = new();
    string outputPath = "";
    string? vectorName = null;
    string llamaUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL") ?? "http://localhost:4000";

    int i = 0;
    for (; i < subArgs.Length; i++)
    {
        switch (subArgs[i])
        {
            case "--help":
                HelpText.PrintEmbedHelp();
                return 0;

            case "--llama-url":
            case "-l":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --llama-url requires a URL.");
                    return 1;
                }
                llamaUrl = subArgs[++i];
                break;

            case "--vector-name":
            case "-v":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --vector-name requires a vector name argument.");
                    return 1;
                }
                vectorName = subArgs[++i];
                break;

            case "--output-dir":
            case "-o":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --output-dir requires a directory path argument.");
                    return 1;
                }
                outputPath = subArgs[++i];
                break;

            case "--input-dir":
            case "-d":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --input-dir requires a directory path argument.");
                    return 1;
                }
                inputDir = subArgs[++i];
                break;

            default:
                // Treat first non-flag arg as input file(s)
                if (!subArgs[i].StartsWith("--"))
                    inputFiles.Add(subArgs[i]);
                else
                {
                    Console.Error.WriteLine($"Error: Unknown option '{subArgs[i]}'.");
                    return 1;
                }
                break;
        }
    }

    // Resolve directory-based input if specified
    if (inputDir is not null)
    {
        if (!Directory.Exists(inputDir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {inputDir}");
            return 1;
        }

        try
        {
            var dirFiles = Directory.GetFiles(inputDir, "*.json", SearchOption.AllDirectories)
                                    .OrderBy(f => f)
                                    .ToList();

            if (dirFiles.Count == 0)
            {
                Console.Error.WriteLine($"Error: No JSON files found in directory: {inputDir}");
                return 1;
            }

            Console.WriteLine($"  Scanning directory: {inputDir} → found {dirFiles.Count} JSON file(s)");
            inputFiles.AddRange(dirFiles);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Error: Access denied scanning directory: {ex.Message}");
            return 1;
        }
    }

    // Validate inputs required
    if (inputFiles.Count == 0 && string.IsNullOrEmpty(inputDir))
    {
        Console.Error.WriteLine(HelpText.EmbedNoInputError);
        Console.WriteLine();
        Console.WriteLine(HelpText.EmbedNoInputUsage.TrimEnd());
        return 1;
    }

    // Validate that input files exist (for direct file args)
    foreach (var f in inputFiles.Where(f => !f.Contains('/')))
    {
        if (!File.Exists(f))
        {
            Console.Error.WriteLine($"Error: Input file not found: {f}");
            return 1;
        }
    }

    // Output directory: use provided path or current dir
    string outputDir = string.IsNullOrEmpty(outputPath) ? "." : outputPath;
    Directory.CreateDirectory(outputDir);

    // Auto-generate merged output filename
    string mergedOutputFile = Path.Combine(outputDir, "embedded.json");

    // ── Configuration ──────────────────────────────────
    var embeddingOptions = new LlamaCppEmbeddingOptions
    {
        ServerUrl = llamaUrl,
        ModelId = Environment.GetEnvironmentVariable("LLAMA_CPP_MODEL"),
        ExpectedDimension = int.TryParse(
            Environment.GetEnvironmentVariable("EMBEDDING_DIMENSION"), out var dim)
            ? dim : 0, // 0 means auto-detect on first response
        VectorName = vectorName,
    };

    // Batch processing for multiple files
    if (inputFiles.Count > 1 || inputDir is not null)
    {
        return await RunBatchEmbedAsync(
            inputFiles.Where(f => f.Contains('/') || File.Exists(f)).ToList(),
            outputDir,
            mergedOutputFile,
            embeddingOptions);
    }

    // ── Single-file mode (backward compat) ────────────
    string singleInput = inputFiles[0];

    Console.WriteLine();
    Console.WriteLine("=== JSON Embedding Pipeline ===");
    Console.WriteLine($"  Input file:   {singleInput}");
    Console.WriteLine($"  Output file:  {mergedOutputFile}");
    Console.WriteLine($"  Server URL:   {embeddingOptions.ServerUrl}");
    Console.WriteLine($"  Model ID:     {embeddingOptions.ModelId ?? "(auto)"}");
    Console.WriteLine();

    string jsonText;
    try
    {
        jsonText = File.ReadAllText(singleInput);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Error: Could not read input file: {ex.Message}");
        return 1;
    }

    JsonNode rootNode;
    try
    {
        rootNode = JsonNode.Parse(jsonText);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Error: Invalid JSON in input file: {ex.Message}");
        return 1;
    }

    if (rootNode is null)
    {
        Console.Error.WriteLine("Error: Could not parse JSON — empty or null document.");
        return 1;
    }

    // ── Create services ────────────────────────────────
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5), // llama.cpp can take time for large chunks
    };

    var embeddingService = new LlamaCppEmbeddingService(embeddingOptions, httpClient);
    var processor = new EmbeddingProcessor(
        embeddingService, 
        embeddingOptions.ExpectedDimension,
        embeddingOptions.VectorName);

    try
    {
        ProcessResult result = await processor.ProcessAsync(
            rootNode,
            singleInput,
            CancellationToken.None);

        // ── Write output ─────────────────────────────────
        try
        {
            EmbeddingProcessor.WriteOutput(result, mergedOutputFile);
            Console.WriteLine();
            Console.WriteLine($"  Output:       {mergedOutputFile}");
            Console.WriteLine("  Completed successfully.");
            return 0;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: Could not write output file: {ex.Message}");
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        return 1;
    }
}

// ─────────────── Batch Embed Orchestrator ───────────────
static async Task<int> RunBatchEmbedAsync(
    List<string> inputFiles,
    string outputDir,
    string mergedOutputFile,
    LlamaCppEmbeddingOptions embeddingOptions)
{
    var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var embeddingService = new LlamaCppEmbeddingService(embeddingOptions, httpClient);

    // Shared state for merging
    var allChunks = new List<JsonNode>();
    var failedFiles = new List<(string file, string error)>();
    int totalChunksProcessed = 0;
    int totalChunksFailed = 0;

    for (int fi = 0; fi < inputFiles.Count; fi++)
    {
        string inputFile = inputFiles[fi];
        Console.WriteLine($"\n[{fi + 1}/{inputFiles.Count}] Processing: {inputFile}");

        try
        {
            string jsonText = File.ReadAllText(inputFile);
            JsonNode rootNode = JsonNode.Parse(jsonText)!;

            var processor = new EmbeddingProcessor(
                embeddingService, 
                embeddingOptions.ExpectedDimension,
                embeddingOptions.VectorName);
            ProcessResult result = await processor.ProcessAsync(rootNode, inputFile, CancellationToken.None);

            // Check per-chunk results for failures
            int fileSuccessCount = result.Results.Count(r => r.Success);
            int fileFailed = result.FailedCount;
            totalChunksProcessed += fileSuccessCount;
            totalChunksFailed += fileFailed;

            // If any chunks failed, write error report
            if (fileFailed > 0)
            {
                string baseName = Path.GetFileNameWithoutExtension(inputFile);
                string errorPath = Path.Combine(outputDir, $"{baseName}.embed_errors.json");

                var errorDoc = new JsonObject
                {
                    ["source_file"] = JsonValue.Create(inputFile),
                    ["error_chunks"] = new JsonArray(),
                    ["success_count"] = JsonValue.Create(fileSuccessCount),
                    ["failure_count"] = JsonValue.Create(fileFailed)
                };

                foreach (var cr in result.Results.Where(c => !c.Success))
                {
                    var errorChunk = new JsonObject
                    {
                        ["id"] = JsonValue.Create(cr.ChunkId),
                        ["content_key"] = JsonValue.Create(cr.ContentKey),
                        ["error"] = JsonValue.Create(cr.Error ?? "Unknown error")
                    };
                    (errorDoc["error_chunks"] as JsonArray)!.Add(errorChunk);
                }

                File.WriteAllText(errorPath, errorDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            // Collect successful chunks for merged output
            var chunks = BatchEmbedHelpers.ExtractChunksForMerge(rootNode);
            foreach (var chunk in chunks)
            {
                allChunks.Add(chunk.DeepClone());
            }

            Console.WriteLine($"  ✓ Processed: {fileSuccessCount}/{result.Results.Count} chunks successful");
        }
        catch (Exception ex)
        {
            string baseName = Path.GetFileNameWithoutExtension(inputFile);
            string errorPath = Path.Combine(outputDir, $"{baseName}.embed_errors.json");

            var errorDoc = new JsonObject
            {
                ["source_file"] = JsonValue.Create(inputFile),
                ["error"] = JsonValue.Create(ex.Message),
                ["success_count"] = JsonValue.Create(0),
                ["failure_count"] = JsonValue.Create(-1) // Unknown total
            };

            File.WriteAllText(errorPath, errorDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            failedFiles.Add((inputFile, ex.Message));
            Console.WriteLine($"  ✗ Error: {ex.Message}");
        }
    }

    httpClient.Dispose();

    // Write merged output if we have any chunks
    if (allChunks.Count > 0)
    {
        var merged = new JsonObject
        {
            ["chunks"] = new JsonArray(allChunks.Select(c => c.DeepClone()).Cast<JsonNode>().ToArray()),
            ["total_chunks"] = JsonValue.Create(totalChunksProcessed),
            ["source_files"] = JsonValue.Create(inputFiles.Count),
            ["successful_chunks"] = JsonValue.Create(totalChunksProcessed)
        };

        File.WriteAllText(mergedOutputFile, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // Summary
    Console.WriteLine($"\n=== Batch Complete ===");
    Console.WriteLine($"  Files processed:   {inputFiles.Count}");
    Console.WriteLine($"  Total chunks:      {totalChunksProcessed + totalChunksFailed}");
    Console.WriteLine($"  Successful:        {totalChunksProcessed}");
    Console.WriteLine($"  Failed:            {totalChunksFailed}");
    if (failedFiles.Any())
    {
        foreach (var (file, err) in failedFiles)
            Console.WriteLine($"  - {file}: {err}");
    }

    return totalChunksFailed > 0 ? 1 : 0;
}

// ─────────────── RAG Command ───────────────
static async Task<int> RunRagAsync(string[] subArgs)
{
    // ── Rag subcommand argument parsing ────────────────
    List<string> inputFiles = new();
    string? inputDir = null;
    string outputDir = "./rag_output";
    string llamaUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL") ?? "http://localhost:4000";
    TimeSpan llamaTimeout = TimeSpan.FromMinutes(10); // Increased default for large files; per-stage limit
    string promptDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "Prompts");

    int i = 0;
    while (i < subArgs.Length)
    {
        switch (subArgs[i])
        {
            case "--help":
                HelpText.PrintRagHelp();
                return 0;

            case "--output-dir":
            case "-o":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --output-dir requires a directory path.");
                    return 1;
                }
                outputDir = subArgs[++i];
                break;

            case "--llama-url":
            case "-l":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --llama-url requires a URL.");
                    return 1;
                }
                llamaUrl = subArgs[++i];
                break;

            case "--prompt-dir":
            case "-p":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --prompt-dir requires a directory path.");
                    return 1;
                }
                promptDir = subArgs[++i];
                break;

            case "--timeout":
                if (i + 1 >= subArgs.Length || !double.TryParse(subArgs[i + 1], out var mins) || mins <= 0)
                {
                    Console.Error.WriteLine("Error: --timeout requires a positive number of minutes.");
                    return 1;
                }
                llamaTimeout = TimeSpan.FromMinutes(mins);
                i++;
                break;

            case "--input-dir":
            case "-d":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --input-dir requires a directory path.");
                    return 1;
                }
                inputDir = subArgs[++i];
                break;

            default:
                // Treat as input markdown file path
                if (!subArgs[i].StartsWith("--"))
                    inputFiles.Add(subArgs[i]);
                else
                {
                    Console.Error.WriteLine($"Error: Unknown option '{subArgs[i]}'.");
                    return 1;
                }
                break;
        }

        i++;
    }

    // Resolve directory-based input if specified
    if (inputDir is not null)
    {
        if (!Directory.Exists(inputDir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {inputDir}");
            return 1;
        }

        try
        {
            var dirFiles = Directory.GetFiles(inputDir, "*.md", SearchOption.AllDirectories)
                                    .Concat(Directory.GetFiles(inputDir, "*.markdown", SearchOption.AllDirectories))
                                    .OrderBy(f => f)
                                    .ToList();

            if (dirFiles.Count == 0)
            {
                Console.Error.WriteLine($"Error: No markdown files found in directory: {inputDir}");
                return 1;
            }

            Console.WriteLine($"  Scanning directory: {inputDir} → found {dirFiles.Count} markdown file(s)");
            inputFiles.AddRange(dirFiles);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Error: Access denied scanning directory: {ex.Message}");
            return 1;
        }
    }

    // Validate inputs
    if (inputFiles.Count == 0)
    {
        Console.Error.WriteLine("Error: At least one markdown file or input directory is required.");
        Console.WriteLine();
        HelpText.PrintRagHelp();
        return 1;
    }

    var validFiles = new List<string>();
    foreach (var f in inputFiles)
    {
        if (!File.Exists(f))
        {
            Console.Error.WriteLine($"Warning: File not found: {f} — skipping.");
        }
        else if (!Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(f).Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Warning: Not a markdown file: {f} — skipping.");
        }
        else
        {
            validFiles.Add(f);
        }
    }

    if (validFiles.Count == 0)
    {
        Console.Error.WriteLine("Error: No valid markdown files to process.");
        return 1;
    }

    // ── Create RAG pipeline ────────────────────────────
    var ragService = new RagService(llamaUrl);
    var processor = new AgenticChunkingProcessor(
        ragService,
        promptDir,
        maxChunkTokens: 8000,
        outputDir: outputDir,
        llamaTimeout: llamaTimeout);

    // ── Process each file ──────────────────────────────
    Console.WriteLine();
    Console.WriteLine("=== Agentic Chunking Pipeline ===");
    Console.WriteLine($"  Server URL:     {llamaUrl}");
    Console.WriteLine($"  Prompt dir:     {promptDir}");
    Console.WriteLine($"  Output dir:     {outputDir}");
    Console.WriteLine($"  Files to process: {validFiles.Count}");
    Console.WriteLine();

    int totalSuccess = 0;
    int totalFailed = 0;
    var results = new List<(string file, bool success, string message)>();

    for (int fi = 0; fi < validFiles.Count; fi++)
    {
        string inputFile = validFiles[fi];
        Console.WriteLine($"[{fi + 1}/{validFiles.Count}] Processing: {inputFile}");

        try
        {
            var result = await processor.ProcessFileAsync(inputFile, CancellationToken.None);
            results.Add((inputFile, true, $"Wrote {result.OutputFile}"));
            totalSuccess++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\u001b[31m  Error: {ex.Message}\u001b[0m");
            results.Add((inputFile, false, ex.Message));
            totalFailed++;
        }

        Console.WriteLine();
    }

    // ── Summary ────────────────────────────────────────
    Console.WriteLine("\n\u001b[1m=== Processing Complete ===\u001b[0m");
    Console.WriteLine($"  Successful: {totalSuccess}");
    Console.WriteLine($"  Failed:     {totalFailed}");

    if (totalFailed > 0)
    {
        Console.WriteLine();
        foreach (var r in results.Where(x => !x.success))
            Console.WriteLine($"  - {r.file}: {r.message}");
    }

    return totalFailed > 0 ? 1 : 0;
}
