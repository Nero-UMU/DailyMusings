using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Topics;

/// <summary>One candidate topic for an input, with the confidence behind it.</summary>
public sealed record TopicSuggestion(TopicId TopicId, string Name, double Score);

/// <summary>
/// Automatic topic recognition (docs/开发指导.md §8.2 step 5: "识别主题并生成 Embedding").
/// <para>
/// Scope is deliberately narrow: §6.2 says the MVP does not have to split a topic, so this only decides
/// which <em>existing</em> topics an input belongs to. It never invents a topic, because a topic set that
/// grows automatically is a topic set the user cannot browse — and §6.2's whole value is rename, merge and
/// re-file over a stable vocabulary.
/// </para>
/// <para>
/// Matching is lexical rather than semantic on purpose. Embedding similarity requires the index to be
/// current, and §8.3 already establishes that the index may be degraded or rebuilding; topic assignment
/// must keep working in exactly that state, since it is the fallback retrieval signal's input.
/// </para>
/// </summary>
public static class TopicMatcher
{
    /// <summary>Below this, a match is noise and the input is left unfiled for the user to sort out.</summary>
    public const double MinimumSuggestionScore = 0.5;

    /// <summary>A match this strong is allowed to become the input's single primary topic (§6.2).</summary>
    public const double PrimaryThreshold = 0.75;

    /// <summary>How many secondary topics automatic recognition may attach at most.</summary>
    public const int MaxSecondaryTopics = 2;

    /// <summary>
    /// Scores every active topic against a text and returns the plausible ones, best first.
    /// Merged topics are excluded: they are tombstones (decision A.9) and must not attract new assignments.
    /// </summary>
    public static IReadOnlyList<TopicSuggestion> Suggest(
        string? text,
        IEnumerable<Topic> topics,
        int maxSuggestions = 3)
    {
        ArgumentNullException.ThrowIfNull(topics);

        if (string.IsNullOrWhiteSpace(text) || maxSuggestions < 1)
        {
            return [];
        }

        var normalisedText = Normalize(text);
        var textTerms = Retrieval.TextSimilarity.Tokenize(text);
        var suggestions = new List<TopicSuggestion>();

        foreach (var topic in topics)
        {
            if (topic.IsMerged || string.IsNullOrWhiteSpace(topic.Name))
            {
                continue;
            }

            var score = Score(topic.Name, normalisedText, textTerms);
            if (score >= MinimumSuggestionScore)
            {
                suggestions.Add(new TopicSuggestion(topic.Id, topic.Name, score));
            }
        }

        return suggestions
            .OrderByDescending(suggestion => suggestion.Score)
            .ThenBy(suggestion => suggestion.Name, StringComparer.Ordinal)

            // Stable tie-break: automatic filing must not depend on the order topics came back from storage.
            .ThenBy(suggestion => suggestion.TopicId.Value)
            .Take(maxSuggestions)
            .ToArray();
    }

    /// <summary>
    /// Turns suggestions into the assignment §6.2 allows: at most one primary, several secondaries.
    /// <para>
    /// Only a strong match becomes primary. A weak match is filed as secondary instead, which keeps "what was
    /// this day mainly about" honest while still making the material findable by tag.
    /// </para>
    /// </summary>
    public static (TopicId? Primary, IReadOnlyList<TopicId> Secondary) ToAssignment(
        IReadOnlyList<TopicSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        TopicId? primary = null;
        var secondary = new List<TopicId>();

        foreach (var suggestion in suggestions)
        {
            if (primary is null && secondary.Count == 0 && suggestion.Score >= PrimaryThreshold)
            {
                primary = suggestion.TopicId;
                continue;
            }

            if (secondary.Count < MaxSecondaryTopics && suggestion.TopicId != primary)
            {
                secondary.Add(suggestion.TopicId);
            }
        }

        return (primary, secondary);
    }

    /// <summary>
    /// Case-folds, strips whitespace and drops punctuation, so that "录音 上传" and "录音、上传" are the same
    /// topic for matching purposes without the stored display name ever being rewritten.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static double Score(string topicName, string normalisedText, IReadOnlySet<string> textTerms)
    {
        var normalisedName = Normalize(topicName);

        // A topic whose whole name appears verbatim is a certain match; coverage would understate a short
        // name inside a long note, and this is the case users actually create topics for.
        if (normalisedName.Length > 0 && normalisedText.Contains(normalisedName, StringComparison.Ordinal))
        {
            return 1.0;
        }

        return Retrieval.TextSimilarity.Coverage(
            Retrieval.TextSimilarity.Tokenize(topicName),
            textTerms);
    }
}
