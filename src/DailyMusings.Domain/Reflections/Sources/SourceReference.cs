using System.Security.Cryptography;
using System.Text;
using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Reflections.Sources;

/// <summary>
/// Verdict for one source reference against the body it was produced for (decision A.6).
/// </summary>
public enum SourceDrift
{
    /// <summary>The quoted range still holds exactly the text it held at generation time.</summary>
    Exact = 0,

    /// <summary>The range or its text moved — the client must fall back to a whole-paragraph hint.</summary>
    Drifted = 1,

    /// <summary>The paragraph itself no longer exists. Treated as drifted for display purposes.</summary>
    Unresolvable = 2,
}

public sealed record SourceDriftResult(SourceReference Source, SourceDrift Drift);

/// <summary>
/// Splits a draft body into paragraphs. Blank-line separated, matching the Markdown the product emits
/// (§11.2), so that "paragraph" means the same thing in the editor, in the source map and in the
/// exported file.
/// </summary>
public static class ParagraphSplitter
{
    public static IReadOnlyList<string> Split(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        return body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.None)
            .Select(block => block.Trim('\n'))
            .ToArray();
    }
}

/// <summary>
/// Content hash of a quoted fragment, used to detect that a paragraph no longer says what it said when
/// the source map was produced (decision A.6).
/// <para>
/// Normalization is deliberately aggressive: case, surrounding whitespace and runs of whitespace are
/// collapsed so that re-wrapping a paragraph does not register as drift, while any real wording change
/// does.
/// </para>
/// </summary>
public static class SourceQuoteHash
{
    public static string Compute(string? quote)
    {
        var normalized = Normalize(quote);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>True when the candidate text hashes to the recorded value.</summary>
    public static bool Matches(string? candidate, string? expectedHash) =>
        !string.IsNullOrEmpty(expectedHash) &&
        string.Equals(Compute(candidate), expectedHash, StringComparison.Ordinal);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}

/// <summary>
/// Maps a paragraph (or a character range inside it) of a draft back to the inputs that justify it
/// (docs/开发指导.md §6.5, decision A.6).
/// </summary>
public sealed class SourceReference
{
    private SourceReference(
        SourceReferenceId id,
        ReflectionVersionId reflectionVersionId,
        int blockIndex,
        int charStart,
        int charEnd,
        string quoteHash,
        InputEntryId inputId,
        double relevance,
        string reason,
        bool isHistorical)
    {
        Id = id;
        ReflectionVersionId = reflectionVersionId;
        BlockIndex = blockIndex;
        CharStart = charStart;
        CharEnd = charEnd;
        QuoteHash = quoteHash;
        InputId = inputId;
        Relevance = relevance;
        Reason = reason;
        IsHistorical = isHistorical;
    }

    public SourceReferenceId Id { get; }

    public ReflectionVersionId ReflectionVersionId { get; }

    /// <summary>0-based paragraph index inside the version body.</summary>
    public int BlockIndex { get; }

    /// <summary>Half-open character range inside that paragraph. <c>[CharStart, CharEnd)</c>.</summary>
    public int CharStart { get; }

    public int CharEnd { get; }

    /// <summary>See <see cref="SourceQuoteHash"/>. The drift detector compares against a fresh hash.</summary>
    public string QuoteHash { get; }

    public InputEntryId InputId { get; }

    /// <summary>Relevance in <c>[0,1]</c> as reported by the retrieval step.</summary>
    public double Relevance { get; }

    public string Reason { get; }

    /// <summary>True when the cited material predates the reflection's content day (§8.3).</summary>
    public bool IsHistorical { get; }

    public static SourceReference Create(
        SourceReferenceId id,
        ReflectionVersionId reflectionVersionId,
        int blockIndex,
        int charStart,
        int charEnd,
        string quotedText,
        InputEntryId inputId,
        double relevance,
        string? reason,
        bool isHistorical)
    {
        if (blockIndex < 0)
        {
            throw new DomainException("source.block_index.negative", "A paragraph index cannot be negative.");
        }

        if (charStart < 0 || charEnd < charStart)
        {
            throw new DomainException(
                "source.range.invalid",
                $"Character range [{charStart},{charEnd}) is not a valid half-open range.");
        }

        if (relevance is < 0 or > 1 || double.IsNaN(relevance))
        {
            throw new DomainException("source.relevance.out_of_range", "Relevance must be within [0,1].");
        }

        if (inputId.IsEmpty)
        {
            throw new DomainException("source.input.required", "A source reference must cite an input.");
        }

        return new SourceReference(
            id,
            reflectionVersionId,
            blockIndex,
            charStart,
            charEnd,
            SourceQuoteHash.Compute(quotedText),
            inputId,
            relevance,
            reason?.Trim() ?? string.Empty,
            isHistorical);
    }

    /// <summary>Rehydrates from storage, keeping the previously stored hash.</summary>
    public static SourceReference Rehydrate(
        SourceReferenceId id,
        ReflectionVersionId reflectionVersionId,
        int blockIndex,
        int charStart,
        int charEnd,
        string quoteHash,
        InputEntryId inputId,
        double relevance,
        string reason,
        bool isHistorical) =>
        new(id, reflectionVersionId, blockIndex, charStart, charEnd, quoteHash, inputId, relevance, reason, isHistorical);
}

/// <summary>
/// Resolves a source reference against the current body text.
/// <para>
/// Why this exists at all: a user may hand-edit a paragraph after generation. If the client kept
/// highlighting by the old offsets it would highlight the <em>wrong sentence</em>, which in a
/// "verify my sources" feature is worse than admitting uncertainty. So a mismatch downgrades the hint
/// to the whole paragraph and tells the user the version was edited (§6.5).
/// </para>
/// </summary>
public static class SourceLocator
{
    public static SourceDrift Check(string? body, SourceReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var paragraphs = ParagraphSplitter.Split(body);
        if (reference.BlockIndex >= paragraphs.Count)
        {
            return SourceDrift.Unresolvable;
        }

        var paragraph = paragraphs[reference.BlockIndex];
        if (reference.CharStart > paragraph.Length || reference.CharEnd > paragraph.Length)
        {
            return SourceDrift.Drifted;
        }

        var quoted = paragraph[reference.CharStart..reference.CharEnd];
        return SourceQuoteHash.Matches(quoted, reference.QuoteHash) ? SourceDrift.Exact : SourceDrift.Drifted;
    }
}

/// <summary>
/// A sentence the second stage could not trace back to any input (docs/开发指导.md §8.4). It is a
/// warning for the user, never a block: they may still publish after confirming.
/// </summary>
public sealed record UnsourcedClaim(int BlockIndex, int CharStart, int CharEnd, string Reason);
