using System.Text.Json;

namespace DailyMusings.Infrastructure.Generation;

/// <summary>
/// Reads a JSON object out of a chat completion's content.
/// <para>
/// The prompt asks for bare JSON, but "OpenAI-compatible" endpoints differ in how literally they honour that:
/// some wrap the object in a Markdown code fence, and some prefix it with a sentence. Failing on either would
/// turn a usable answer into a permanent generation failure, so the first balanced object in the text is
/// extracted and parsed. Nothing is inferred or repaired beyond that — a payload that is not valid JSON is still
/// reported as unreadable, because guessing at the model's intent is exactly what this product must not do with
/// the user's own writing.
/// </para>
/// </summary>
internal static class JsonPayloadReader
{
    public static T? TryRead<T>(string? content)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var candidate in Candidates(content))
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(candidate);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Not this slice; try the next one.
            }
        }

        return null;
    }

    /// <summary>
    /// The whole text first, then the first balanced object inside it. Braces inside strings are skipped so that
    /// a body containing "}" — which a draft about code plausibly does — cannot truncate the object early.
    /// </summary>
    private static IEnumerable<string> Candidates(string content)
    {
        var trimmed = content.Trim();

        // Strip a code fence if the whole answer is one.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');
            if (firstBreak > 0)
            {
                var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (closing > firstBreak)
                {
                    trimmed = trimmed[(firstBreak + 1)..closing].Trim();
                }
            }
        }

        yield return trimmed;

        var start = trimmed.IndexOf('{');
        if (start < 0)
        {
            yield break;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        var slice = trimmed[start..(i + 1)];
                        if (slice.Length != trimmed.Length)
                        {
                            yield return slice;
                        }

                        yield break;
                    }

                    break;
            }
        }
    }
}
