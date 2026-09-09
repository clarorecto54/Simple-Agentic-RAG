namespace Embedding_Console.Services;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Sends structured prompts with max-reasoning effort to an OpenAI-API-compatible chat endpoint.
/// Uses reasoning_tokens: -1 for unlimited budget (Qwen format).
/// </summary>
public class RagService : IRagService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public RagService(string serverUrl)
    {
        _baseUrl = serverUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15), // Reasoning can take time
        };
    }

    /// <summary>
    /// Sends a request with max reasoning effort. The prompt is split into system and user messages,
    /// where lines starting with "# " at the top are treated as system context.
    /// </summary>
    public async Task<string> SendAsync(string prompt)
    {
        // Split prompt: everything before the first double line-break that looks like instructions goes to system,
        // the rest goes to user. For our structured prompts, we'll pass as user message with explicit role separation.
        var messages = new[]
        {
            new
            {
                role = "system",
                content = "You are a precise markdown processing agent. Return ONLY valid JSON — no markdown fences, no explanations, no commentary."
            },
            new
            {
                role = "user",
                content = prompt
            }
        };

        var requestBody = new
        {
            model = "",
            messages = messages,
            temperature = 0.1,    // Deterministic output
            top_p = 0.9,
            max_tokens = -1,       // Unlimited tokens for long outputs
            reasoning_tokens = -1, // Max reasoning budget (Qwen format)
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/v1/chat/completions",
            requestBody).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new RagServiceException(
                $"Chat endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {errorBody}",
                (int)response.StatusCode);
        }

        // Parse the response — look for choices[0].message.content or reasoning_content
        var jsonText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choicesArray) ||
            choicesArray.GetArrayLength() < 1)
        {
            throw new RagServiceException(
                "No choices in chat completion response — invalid format.",
                (int)response.StatusCode);
        }

        var choice = choicesArray[0];
        if (!choice.TryGetProperty("message", out var message))
        {
            throw new RagServiceException(
                "Response missing 'message' field — invalid format.",
                (int)response.StatusCode);
        }

        string rawContent = "";
        // Prefer reasoning_content first, fall back to regular content
        if (message.TryGetProperty("reasoning_content", out var reasoningProp))
        {
            rawContent = reasoningProp.GetString() ?? message.GetProperty("content").GetString() ?? "";
        }
        else
        {
            rawContent = message.GetProperty("content").GetString() ?? "";
        }

        // Extract JSON from response — Qwen may prepend reasoning text like "Here's a thinking process:"
        string content = ExtractJsonFromText(rawContent);

        return content;
    }

    /// <summary>
    /// Finds and extracts a valid JSON object or array from arbitrary text.
    /// Handles cases where the LLM prepends reasoning or commentary before the JSON block.
    /// When multiple balanced brace/bracket blocks exist, tries them from innermost to
    /// outermost so template placeholders (e.g. "..." in examples) are skipped for real data.
    /// </summary>
    private static string ExtractJsonFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Fast path: the whole response is valid JSON
        try
        {
            JsonNode.Parse(text.Trim());
            return text.Trim();
        }
        catch { /* not clean JSON */ }

        // Collect all balanced block positions, then sort by depth (innermost first)
        var candidates = new List<(int openIdx, int closeIdx, char openChar, string raw)>();

        foreach (char targetOpen in new[] { '{', '[' })
        {
            char closeChar = targetOpen == '{' ? '}' : ']';
            int pos = 0;

            while ((pos = text.IndexOf(targetOpen, pos)) >= 0)
            {
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int startIdx = pos;

                for (int i = pos; i < text.Length; i++)
                {
                    char c = text[i];

                    if (escaped) { escaped = false; continue; }

                    if (c == '\\' && inString) { escaped = true; continue; }

                    if (c == '"') { inString = !inString; continue; }

                    if (inString) continue;

                    if (c == targetOpen) depth++;
                    else if (c == closeChar)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            candidates.Add((startIdx, i, targetOpen, ""));
                            pos = i + 1; // search after this block for inner blocks
                            break;
                        }
                    }
                }

                if (pos < text.Length) pos++; // advance past this opening bracket to find the next candidate
            }
        }

        if (candidates.Count == 0) return text.Trim();

        // Sort by: 1) span descending (outermost first), 2) object before array (prompts expect objects).
        candidates.Sort((a, b) =>
        {
            int spanCompare = (b.closeIdx - b.openIdx).CompareTo(a.closeIdx - a.openIdx);
            if (spanCompare != 0) return spanCompare;
            // Prefer '{' over '[' at equal size — object structures are more likely the target
            return a.openChar.CompareTo(b.openChar);
        });

        foreach (var candidate in candidates)
        {
            string block = text.Substring(candidate.openIdx, candidate.closeIdx - candidate.openIdx + 1);
            try
            {
                JsonNode.Parse(block);
                return block.Trim();
            }
            catch { /* invalid JSON at this level, try the next */ }
        }

        // Fallback: return everything from first opening brace to last closing bracket
        if (candidates.Count > 0)
        {
            var min = candidates.Min(c => c.openIdx);
            var max = candidates.Max(c => c.closeIdx);
            return text.Substring(min, max - min + 1).Trim();
        }

        return text.Trim();
    }

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// Represents an error from the RAG service.
/// </summary>
public class RagServiceException : Exception
{
    public int StatusCode { get; }

    public RagServiceException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
