using System.Text;

namespace DailyMusings.Domain.Reflections.Sources;

/// <summary>A quote resolved to its exact position inside a draft body.</summary>
public sealed record LocatedQuote(int BlockIndex, int CharStart, int CharEnd, string Text);

/// <summary>
/// Finds the paragraph and character range a quoted fragment occupies
/// (docs/开发指导.md §6.5, decision A.6).
/// <para>
/// Why locate instead of trusting the model: the source map needs a paragraph index, a half-open character
/// range and a hash of the exact quoted text. Asking a language model to emit character offsets produces
/// offsets that are wrong by a few characters often enough to make highlighting untrustworthy — and in a
/// "verify my sources" feature, pointing at the wrong sentence is worse than pointing at nothing. So the
/// model is asked only for the quote, and the offsets are derived here from the text it actually produced.
/// </para>
/// <para>
/// A quote that cannot be found is reported as unresolved rather than approximated. Callers drop it, which
/// is visible in the source count instead of silently producing a citation nobody can follow.
/// </para>
/// </summary>
public static class SourceQuoteLocator
{
    public static LocatedQuote? Locate(string? body, string? quote)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }

        var trimmed = quote.Trim();
        var paragraphs = ParagraphSplitter.Split(body);

        for (var blockIndex = 0; blockIndex < paragraphs.Count; blockIndex++)
        {
            var paragraph = paragraphs[blockIndex];

            var exact = paragraph.IndexOf(trimmed, StringComparison.Ordinal);
            if (exact >= 0)
            {
                return new LocatedQuote(blockIndex, exact, exact + trimmed.Length, paragraph.Substring(exact, trimmed.Length));
            }

            // Models re-wrap and re-space text constantly. A whitespace-insensitive match recovers a quote
            // that is otherwise identical, and the recorded range still points at the paragraph's own text —
            // never at the model's rendering of it.
            var loose = LocateIgnoringWhitespace(paragraph, trimmed);
            if (loose is { } range)
            {
                return new LocatedQuote(
                    blockIndex,
                    range.Start,
                    range.End,
                    paragraph[range.Start..range.End]);
            }
        }

        return null;
    }

    private static (int Start, int End)? LocateIgnoringWhitespace(string paragraph, string quote)
    {
        var pattern = Normalize(quote);
        if (pattern.Length == 0)
        {
            return null;
        }

        // Walk the paragraph keeping a map from normalized position back to the original index, so a match in
        // normalized space can be turned into exact offsets in the real text.
        var normalized = new StringBuilder(paragraph.Length);
        var positions = new List<int>(paragraph.Length);

        for (var i = 0; i < paragraph.Length; i++)
        {
            var ch = paragraph[i];
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            normalized.Append(char.ToLowerInvariant(ch));
            positions.Add(i);
        }

        var index = normalized.ToString().IndexOf(pattern, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = positions[index];
        var end = positions[index + pattern.Length - 1] + 1;
        return (start, end);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
