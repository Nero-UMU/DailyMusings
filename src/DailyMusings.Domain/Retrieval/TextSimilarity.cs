using System.Text;

namespace DailyMusings.Domain.Retrieval;

/// <summary>
/// Language-agnostic text similarity, used by the degraded half of history retrieval (§8.3) and by
/// automatic topic recognition (§8.2).
/// <para>
/// Why not word tokens: the product's content is Chinese, which has no spaces, and §20 forbids pulling in a
/// segmentation dependency for a feature that only needs a ranking signal. Character bigrams over CJK runs
/// and lowercased words over Latin runs need no dictionary at all, behave identically on both scripts, and
/// are a pure function of their input — which is what makes the whole retrieval path unit-testable.
/// </para>
/// <para>
/// The measure is Jaccard over those term sets. It is deliberately symmetric: a long past entry that merely
/// contains one shared word does not outrank a short past entry that is about the same thing.
/// </para>
/// </summary>
public static class TextSimilarity
{
    /// <summary>Term set of a text: CJK character bigrams plus lowercased Latin/digit words.</summary>
    public static IReadOnlySet<string> Tokenize(string? text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return terms;
        }

        var run = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch) || char.IsLetterOrDigit(ch))
            {
                run.Append(ch);
                continue;
            }

            Flush(run, terms);
        }

        Flush(run, terms);
        return terms;
    }

    /// <summary>Jaccard similarity of two texts, in <c>[0,1]</c>. Two empty texts score 0, not 1.</summary>
    public static double Jaccard(string? left, string? right) =>
        Jaccard(Tokenize(left), Tokenize(right));

    /// <summary>Jaccard similarity of two already-tokenized texts, in <c>[0,1]</c>.</summary>
    public static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        // Iterate the smaller set: the intersection can never be larger than it.
        var (smaller, larger) = left.Count <= right.Count ? (left, right) : (right, left);

        var shared = 0;
        foreach (var term in smaller)
        {
            if (larger.Contains(term))
            {
                shared++;
            }
        }

        if (shared == 0)
        {
            return 0;
        }

        return (double)shared / (left.Count + right.Count - shared);
    }

    /// <summary>
    /// How much of <paramref name="needle"/> is covered by <paramref name="haystack"/>, in <c>[0,1]</c>.
    /// <para>
    /// Jaccard alone punishes a short query matched against a long document, which is exactly the shape of
    /// "does this long past note talk about the two words I just said?". Coverage answers that question
    /// directly, so topic-suggestion scoring uses it.
    /// </para>
    /// </summary>
    public static double Coverage(string? needle, string? haystack) =>
        Coverage(Tokenize(needle), Tokenize(haystack));

    /// <summary>Coverage of an already-tokenized needle by an already-tokenized haystack.</summary>
    public static double Coverage(IReadOnlySet<string> needle, IReadOnlySet<string> haystack)
    {
        ArgumentNullException.ThrowIfNull(needle);
        ArgumentNullException.ThrowIfNull(haystack);

        if (needle.Count == 0 || haystack.Count == 0)
        {
            return 0;
        }

        var shared = 0;
        foreach (var term in needle)
        {
            if (haystack.Contains(term))
            {
                shared++;
            }
        }

        return (double)shared / needle.Count;
    }

    private static void Flush(StringBuilder run, HashSet<string> terms)
    {
        if (run.Length == 0)
        {
            return;
        }

        var text = run.ToString();
        run.Clear();

        if (IsCjk(text[0]))
        {
            if (text.Length == 1)
            {
                terms.Add(text);
                return;
            }

            for (var i = 0; i + 1 < text.Length; i++)
            {
                terms.Add(text.Substring(i, 2));
            }

            return;
        }

        terms.Add(text.ToLowerInvariant());
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u3400' and <= '\u9FFF' or // CJK unified ideographs + extension A
            >= '\uF900' and <= '\uFAFF' or // compatibility ideographs
            >= '\u3040' and <= '\u30FF'; // kana, so Japanese notes behave the same way
}
