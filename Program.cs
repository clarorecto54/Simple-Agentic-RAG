using Embedding_Console;
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
    string inputPath = "";
    string outputPath = ""; // sentinel: auto-generate if empty

    int i = 0;
    for (; i < subArgs.Length; i++)
    {
        switch (subArgs[i])
        {
            case "--help":
                HelpText.PrintEmbedHelp();
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
        Console.Error.WriteLine(HelpText.EmbedNoInputError);
        Console.WriteLine();
        Console.WriteLine(HelpText.EmbedNoInputUsage.TrimEnd());
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
    string? inputDir = null;
    string outputDir = "./rag_output";
    string llamaUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL") ?? "http://localhost:4000";
    string promptDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "Reference");

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
