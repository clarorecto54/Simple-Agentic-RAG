using Embedding_Console.Processors;
using Embedding_Console.Services;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;

// ─────────────── CLI Argument Parsing ───────────────
if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run -- <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  embed   Embed chunks from a JSON file via llama.cpp.");
    Console.WriteLine("  rag     Agentic chunking: markdown → segmented → semantic → RAG-ready chunks.");
    Console.WriteLine();
    Console.WriteLine("  Use 'embed --help' or 'rag --help' for command-specific options.");
    return 1;
}

string command = args[0];

if (command == "--help" || command == "-h")
{
    PrintHelp();
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
    string inputPath = "";
    string outputPath = ""; // sentinel: auto-generate if empty

    int i = 0;
    for (; i < subArgs.Length; i++)
    {
        switch (subArgs[i])
        {
            case "--help":
                Console.WriteLine("Usage: dotnet run -- embed <input.json> [output.json]");
                Console.WriteLine();
                Console.WriteLine("  input.json   Path to the JSON file with chunks to embed.");
                Console.WriteLine("  output.json  Optional. Path for the embedded output file.");
                Console.WriteLine();
                Console.WriteLine("If output path is omitted, <input>.embedded.json will be generated.");
                return 0;

            case "--output":
            case "-o":
                if (i + 1 >= subArgs.Length)
                {
                    Console.Error.WriteLine("Error: --output requires a file path argument.");
                    return 1;
                }
                outputPath = subArgs[++i];
                break;

            default:
                // Treat first non-flag arg as inputPath
                if (string.IsNullOrEmpty(inputPath))
                    inputPath = subArgs[i];
                else if (!string.IsNullOrEmpty(outputPath))
                {
                    Console.Error.WriteLine($"Error: Unexpected argument '{subArgs[i]}'.");
                    return 1;
                }
                else
                    outputPath = subArgs[i];
                break;
        }
    }

    // Validate input file required
    if (string.IsNullOrEmpty(inputPath))
    {
        Console.Error.WriteLine("Error: Input file path is required.");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run -- embed <input.json> [output.json]");
        Console.WriteLine("  or:   dotnet run -- embed --help");
        return 1;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
        return 1;
    }

    // Auto-generate output path: chunks.json → chunks.embedded.json
    if (string.IsNullOrEmpty(outputPath))
    {
        var dir = Path.GetDirectoryName(inputPath) ?? ".";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        outputPath = Path.Combine(dir, $"{nameWithoutExt}.embedded.json");
    }

    // ── Configuration ──────────────────────────────────
    var embeddingOptions = new LlamaCppEmbeddingOptions
    {
        ServerUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL")
                    ?? "http://localhost:4000",
        ModelId = Environment.GetEnvironmentVariable("LLAMA_CPP_MODEL"),
        ExpectedDimension = int.TryParse(
            Environment.GetEnvironmentVariable("EMBEDDING_DIMENSION"), out var dim)
            ? dim : 0, // 0 means auto-detect on first response
    };

    // ── Load JSON ──────────────────────────────────────
    string jsonText;
    try
    {
        jsonText = File.ReadAllText(inputPath);
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
    var processor = new EmbeddingProcessor(embeddingService, embeddingOptions.ExpectedDimension);

    // ── Process ────────────────────────────────────────
    try
    {
        Console.WriteLine();
        Console.WriteLine("=== JSON Embedding Pipeline ===");
        Console.WriteLine($"  Input file:   {inputPath}");
        Console.WriteLine($"  Output file:  {outputPath}");
        Console.WriteLine($"  Server URL:   {embeddingOptions.ServerUrl}");
        Console.WriteLine($"  Model ID:     {embeddingOptions.ModelId ?? "(auto)"}");
        Console.WriteLine();

        ProcessResult result = await processor.ProcessAsync(
            rootNode,
            inputPath,
            CancellationToken.None);

        // ── Write output ─────────────────────────────────
        try
        {
            EmbeddingProcessor.WriteOutput(result, outputPath);
            Console.WriteLine();
            Console.WriteLine($"  Output:       {outputPath}");
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

// ─────────────── RAG Command ───────────────
static async Task<int> RunRagAsync(string[] subArgs)
{
    // ── Rag subcommand argument parsing ────────────────
    List<string> inputFiles = new();
    string outputDir = "./rag_output";
    string llamaUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL") ?? "http://localhost:4000";
    string promptDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "Reference");

    int i = 0;
    while (i < subArgs.Length)
    {
        switch (subArgs[i])
        {
            case "--help":
                PrintRagHelp();
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

    // Validate inputs
    if (inputFiles.Count == 0)
    {
        Console.Error.WriteLine("Error: At least one markdown file is required.");
        Console.WriteLine();
        PrintRagHelp();
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
        outputDir: outputDir);

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
            Console.WriteLine($"  \033[31mError: {ex.Message}\033[0m");
            results.Add((inputFile, false, ex.Message));
            totalFailed++;
        }

        Console.WriteLine();
    }

    // ── Summary ────────────────────────────────────────
    Console.WriteLine("\n\033[1m=== Processing Complete ===\033[0m");
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

static void PrintRagHelp()
{
    Console.WriteLine("Usage: dotnet run -- rag <file1.md> [file2.md ...] [options]");
    Console.WriteLine();
    Console.WriteLine("Agentic chunking pipeline: markdown → segmented → semantic analysis → RAG-ready chunks.");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  file1.md ...     One or more markdown files to process.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --output-dir, -o DIR   Output directory for .ragged.json files (default: ./rag_output)");
    Console.WriteLine("  --llama-url, -l URL    llama.cpp server URL (default: $LLAMA_CPP_URL or http://localhost:4000)");
    Console.WriteLine("  --prompt-dir, -p DIR   Directory containing prompt templates (default: ./Reference)");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  LLAMA_CPP_URL       llama.cpp server URL");
    Console.WriteLine();
    Console.WriteLine("Each file produces a <filename>.ragged.json in the output directory.");
    Console.WriteLine("Intermediate results are saved per-stage so OOM errors preserve progress.");
}

static void PrintHelp()
{
    Console.WriteLine("Usage: dotnet run -- <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  embed   Embed chunks from a JSON file via llama.cpp.");
    Console.WriteLine("  rag     Agentic chunking: markdown → segmented → semantic → RAG-ready chunks.");
    Console.WriteLine();
    Console.WriteLine("Use 'embed --help' or 'rag --help' for command-specific options.");
}
