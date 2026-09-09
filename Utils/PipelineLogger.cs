namespace Embedding_Console.Utils;

using System.Collections.Concurrent;

/// <summary>
/// Provides structured logging for the agentic chunking pipeline.
/// Writes per-file debug logs capturing inputs, outputs, and errors at every stage boundary.
/// </summary>
public static class PipelineLogger
{
    private static readonly ConcurrentDictionary<string, List<string>> _logs = new();

    /// <summary>
    /// Gets or creates the log entries for a given source file (safe for concurrent access).
    /// </summary>
    private static List<string> GetLog(string sourceFile) =>
        _logs.GetOrAdd(sourceFile, _ => new List<string>());

    /// <summary>
    /// Writes an entry to the per-file log. Entries are timestamped and tagged by stage.
    /// </summary>
    public static void WriteEntry(string sourceFile, string tag, string message)
    {
        var ts = DateTime.UtcNow.ToString("O"); // ISO 8601 round-trip for deterministic sorting
        GetLog(sourceFile).Add($"[{ts}] [{tag}] {message}");
    }

    /// <summary>
    /// Writes structured input payload to the log (truncated if too long).
    /// </summary>
    public static void WriteInput(string sourceFile, string stageName, string content)
    {
        var truncated = Truncate(content, 40_000);
        WriteEntry(sourceFile, $"INPUT:{stageName}", $"Length: {content.Length} chars (truncated to 40k for log)\n{truncated}");
    }

    /// <summary>
    /// Writes structured output payload from an LLM call.
    /// </summary>
    public static void WriteOutput(string sourceFile, string stageName, string content)
    {
        var truncated = Truncate(content, 40_000);
        WriteEntry(sourceFile, $"OUTPUT:{stageName}", $"Length: {content.Length} chars (truncated to 40k for log)\n{truncated}");
    }

    /// <summary>
    /// Writes an error with full context to the log.
    /// </summary>
    public static void WriteError(string sourceFile, string stageName, Exception ex, string? context = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Exception type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine($"Target site: {ex.TargetSite}");
        sb.AppendLine($"Stack trace:\n{ex.StackTrace}");

        var current = ex.InnerException;
        while (current != null)
        {
            sb.AppendLine($"\n--- Inner ---\nType: {current.GetType().FullName}\nMessage: {current.Message}\n{current.StackTrace}");
            current = current.InnerException;
        }

        if (!string.IsNullOrEmpty(context))
            sb.AppendLine($"\nContext:\n{context}");

        WriteEntry(sourceFile, $"ERROR:{stageName}", sb.ToString());
    }

    /// <summary>
    /// Writes the full log for a source file to disk as a .log.txt file.
    /// </summary>
    public static void FlushToFile(string sourceFile, string outputDirectory)
    {
        if (!_logs.TryRemove(sourceFile, out var entries)) return;

        if (!entries.Any()) return;

        Directory.CreateDirectory(outputDirectory);
        var safeName = Path.GetFileNameWithoutExtension(sourceFile)
            .ToLower().Replace(' ', '-').Replace('.', '_');
        var logPath = Path.Combine(outputDirectory, $"{safeName}_pipeline.log.txt");

        File.WriteAllLines(logPath, entries);
    }

    /// <summary>
    /// Clears all cached logs (call at the start of a fresh run).
    /// </summary>
    public static void Clear() => _logs.Clear();

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        return s[..maxChars] + $"\n... [TRUNCATED: original was {s.Length} chars]";
    }
}
