using System;

namespace Embedding_Console;

/// <summary>
/// Centralised help text for CLI commands. Keeps Program.cs clean and makes
/// usage messages easy to find, edit, and translate later.
/// </summary>
internal static class HelpText
{
    // ── Main CLI help ────────────────────────────────────────
    internal const string MainUsage = "dotnet run -- <command> [options]";

    internal const string MainDescription =
        """
        Commands:
          embed   Embed chunks from a JSON file via llama.cpp.
          rag     Agentic chunking: markdown → segmented → semantic → RAG-ready chunks.

        Use 'embed --help' or 'rag --help' for command-specific options.
        """;

    // ── Embed command help ───────────────────────────────────
    internal const string EmbedUsage = "dotnet run -- embed <input.json> [output.json]";

    internal const string EmbedInlineHelp =
        """
        Usage: dotnet run -- embed <input.json> [output.json]

          input.json   Path to the JSON file with chunks to embed.
          output.json  Optional. Path for the embedded output file.

        If output path is omitted, <input>.embedded.json will be generated.
        """;

    internal const string EmbedNoInputError = "Error: Input file path is required.";

    internal const string EmbedNoInputUsage =
        """
        Usage: dotnet run -- embed <input.json> [output.json]
          or:   dotnet run -- embed --help
        """;

    // ── RAG command help ─────────────────────────────────────
    internal const string RagDescription =
        """
        Agentic chunking pipeline: markdown → segmented → semantic analysis → RAG-ready chunks.
        Specify inputs as individual files OR use --input-dir to scan a directory for .md files.
        """;

    internal const string RagArgumentsSection =
        """
        Arguments (optional):
          file1.md ...     One or more markdown files to process.
        """;

    internal const string RagOptionsSection =
        """
        Options:
          --input-dir, -d DIR      Directory to scan for .md/.markdown files (recursive)
          --output-dir, -o DIR     Output directory for .ragged.json files (default: ./rag_output)
          --llama-url, -l URL      llama.cpp server URL (default: $LLAMA_CPP_URL or http://localhost:4000)
          --prompt-dir, -p DIR     Directory containing prompt templates (default: ./Reference)
          --timeout MIN            Per-stage timeout in minutes (default: 10); increase for large files
        """;

    internal const string RagEnvSection =
        """
        Environment variables:
          LLAMA_CPP_URL       llama.cpp server URL
        """;

    internal const string RagFooter =
        """
        Each file produces a <filename>.ragged.json in the output directory.
        Intermediate results are saved per-stage so OOM errors preserve progress.
        """;

    // ── Convenience builders ─────────────────────────────────
    /// <summary>
    /// Prints (via <c>Console.WriteLine</c>) the full main help to stdout.
    /// </summary>
    internal static void PrintMain()
    {
        Console.WriteLine(MainUsage);
        Console.WriteLine();
        Console.WriteLine(MainDescription);
    }

    /// <summary>
    /// Prints the embed command's inline help to stdout.
    /// </summary>
    internal static void PrintEmbedHelp()
    {
        Console.WriteLine(EmbedInlineHelp.Trim());
    }

    /// <summary>
    /// Prints the full rag subcommand help to stdout.
    /// </summary>
    internal static void PrintRagHelp()
    {
        Console.WriteLine("Usage: dotnet run -- rag [options]");
        Console.WriteLine();
        Console.WriteLine(RagDescription);
        Console.WriteLine();
        Console.WriteLine(RagArgumentsSection.TrimEnd());
        Console.WriteLine();
        Console.WriteLine(RagOptionsSection.TrimEnd());
        Console.WriteLine();
        Console.WriteLine(RagEnvSection.TrimEnd());
        Console.WriteLine();
        Console.WriteLine(RagFooter.TrimEnd());
    }
}
