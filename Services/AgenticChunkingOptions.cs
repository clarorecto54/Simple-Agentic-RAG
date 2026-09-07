namespace Embedding_Console.Services;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Configuration for the agentic chunking pipeline.
/// </summary>
public class AgenticChunkingOptions
{
    /// <summary>
    /// Base URL of the llama.cpp server (e.g., http://localhost:4000).
    /// </summary>
    public string ServerUrl { get; set; } = "http://localhost:4000";

    /// <summary>
    /// Path to prompt templates. Defaults to "./Reference" relative to project root.
    /// </summary>
    public string PromptDirectory { get; set; } = "./Reference";

    /// <summary>
    /// Maximum tokens per markdown chunk before splitting (default 8000).
    /// The llama.cpp model has ~100K context; we leave headroom.
    /// </summary>
    public int MaxChunkTokens { get; set; } = 8000;

    /// <summary>
    /// Output directory for intermediate and final files. Defaults to "rag_output".
    /// </summary>
    public string OutputDirectory { get; set; } = "./rag_output";
}

/// <summary>
/// Intermediate format between Stage 1 (Segment) and Stage 2 (Semantic).
/// Each segment gets its own markdown content extracted from the source.
/// </summary>
public record SegmentedFile(
    string Id,
    string Filename,
    string Title,
    IReadOnlyList<string> HeadingPath,
    string SourceContent);

/// <summary>
/// Result of a single file's agentic chunking pipeline.
/// </summary>
public record RagingResult(
    string SourceFile,
    string OutputFile,
    int Stage1Segments,
    int Stage2Chunks,
    int Stage3FinalChunks,
    TimeSpan Duration);
