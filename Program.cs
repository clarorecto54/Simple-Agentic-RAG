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
    Console.WriteLine("  embed  Embed chunks from a JSON file.");
    Console.WriteLine();
    Console.WriteLine("  Use 'embed --help' for command-specific options.");
    return 1;
}

string command = args[0];

switch (command)
{
    case "embed":
        return await RunEmbedAsync(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"Error: Unknown command '{command}'.");
        Console.WriteLine();
        Console.WriteLine("Available commands: embed");
        Console.WriteLine();
        Console.WriteLine("Use 'dotnet run -- --help' for usage.");
        return 1;
}

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
