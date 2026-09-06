using Embedding_Console.Processors;
using Embedding_Console.Services;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;

// ─────────────── CLI Argument Parsing ───────────────
string inputPath;
string outputPath;

if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run -- <input.json> [output.json]");
    Console.WriteLine();
    Console.WriteLine("  input.json   Path to the JSON file with chunks to embed.");
    Console.WriteLine("  output.json  Optional. Path for the embedded output file.");
    Console.WriteLine();
    Console.WriteLine("If output path is omitted, <input>.embedded.json will be generated.");
    return 1;
}

inputPath = args[0];

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
    return 1;
}

if (args.Length >= 2)
{
    outputPath = args[1];
}
else
{
    // Auto-generate output path: chunks.json → chunks.embedded.json
    var dir = Path.GetDirectoryName(inputPath) ?? ".";
    var nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
    outputPath = Path.Combine(dir, $"{nameWithoutExt}.embedded.json");
}

// ─────────────── Configuration ───────────────
var embeddingOptions = new LlamaCppEmbeddingOptions
{
    ServerUrl = Environment.GetEnvironmentVariable("LLAMA_CPP_URL") 
                ?? "http://localhost:4000",
    ModelId = Environment.GetEnvironmentVariable("LLAMA_CPP_MODEL"),
    ExpectedDimension = int.TryParse(
        Environment.GetEnvironmentVariable("EMBEDDING_DIMENSION"), out var dim)
        ? dim : 0, // 0 means auto-detect on first response
};

// ─────────────── Load JSON ───────────────
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

// ─────────────── Create Services ───────────────
var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(5), // llama.cpp can take time for large chunks
};

var embeddingService = new LlamaCppEmbeddingService(embeddingOptions, httpClient);
var processor = new EmbeddingProcessor(embeddingService, embeddingOptions.ExpectedDimension);

// ─────────────── Process ───────────────
ProcessResult result;
try
{
    Console.WriteLine();
    Console.WriteLine("=== JSON Embedding Pipeline ===");
    Console.WriteLine($"  Input file:   {inputPath}");
    Console.WriteLine($"  Output file:  {outputPath}");
    Console.WriteLine($"  Server URL:   {embeddingOptions.ServerUrl}");
    Console.WriteLine($"  Model ID:     {embeddingOptions.ModelId ?? "(auto)"}");
    Console.WriteLine();

    result = await processor.ProcessAsync(
        rootNode, 
        inputPath, 
        CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return 1;
}

// ─────────────── Write Output ───────────────
try
{
    EmbeddingProcessor.WriteOutput(result, outputPath);
    Console.WriteLine();
    Console.WriteLine($"  Output:       {outputPath}");
    Console.WriteLine("  Completed successfully.");
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Error: Could not write output file: {ex.Message}");
    return 1;
}

httpClient.Dispose();
return 0;
