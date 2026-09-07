namespace Embedding_Console.Services;

/// <summary>
/// Sends a structured prompt with reasoning effort to an OpenAI-API-compatible chat endpoint.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Sends a request to the chat/completions endpoint with max-reasoning effort.
    /// </summary>
    /// <param name="prompt">The full prompt text (acts as system + user message).</param>
    /// <returns>The raw response string from the LLM.</returns>
    Task<string> SendAsync(string prompt);
}
