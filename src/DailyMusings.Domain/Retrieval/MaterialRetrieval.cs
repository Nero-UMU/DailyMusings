using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Retrieval;

/// <summary>
/// Which retrieval path produced a candidate. Ordering matters: when two signals find the same entry, the
/// earlier one wins the tie, and semantic retrieval is the most expensive and most specific signal.
/// </summary>
public enum RetrievalSignal
{
    /// <summary>Embedding similarity. Only present while the index matches the configured model (§8.3).</summary>
    Semantic = 0,

    /// <summary>Shared topic assignment. Always available — it needs no external service.</summary>
    Topic = 1,

    /// <summary>Full-text similarity, the second half of the documented degradation path (§8.3).</summary>
    FullText = 2,
}

/// <summary>One candidate handed to <see cref="RetrievalPolicy"/>, with its score and provenance.</summary>
public sealed record RetrievalCandidate(InputEntry Entry, double Score, string Reason, RetrievalSignal Signal);

/// <summary>Exactly the material a generation run may cite, after ranking and truncation (§8.3).</summary>
public sealed record RetrievedMaterial(InputEntry Entry, double Relevance, string Reason, bool IsHistorical);

/// <summary>
/// Retrieval tuning. Defaults are the documented ones: ten high-relevance items at most (§8.3).
/// </summary>
public sealed record RetrievalLimits(
    int MaxMaterials,
    double MinimumRelevance,
    double MinimumLexicalScore)
{
    public static RetrievalLimits Default { get; } = new(
        MaxMaterials: 10,
        MinimumRelevance: 0.05,
        MinimumLexicalScore: 0.08);

    public void Validate()
    {
        if (MaxMaterials < 1)
        {
            throw new DomainException("retrieval.max_materials.out_of_range", "At least one material must be allowed.");
        }

        if (MinimumRelevance is < 0 or > 1 || MinimumLexicalScore is < 0 or > 1)
        {
            throw new DomainException("retrieval.threshold.out_of_range", "Retrieval thresholds must be within [0,1].");
        }
    }
}

/// <summary>
/// Decides which past inputs a day's reflection may be built from (docs/开发指导.md §8.3).
/// <para>
/// The hard boundary — "nothing from after the article's content day" — lives in
/// <see cref="RecallPolicy"/> and is re-applied here rather than trusted, because every path into retrieval
/// (semantic index, topic lookup, full-text scan) could otherwise reintroduce a future entry through its own
/// query. The promise this protects is the product's whole premise: a reflection never presents something as
/// having happened on a day it did not happen.
/// </para>
/// </summary>
public static class RetrievalPolicy
{
    /// <summary>
    /// Merges the three signals into the final, ordered material list.
    /// </summary>
    /// <param name="semanticCandidates">Embedding hits. Pass an empty list while the index is rebuilding.</param>
    /// <param name="topicCandidates">Topic-tag hits. Always passed: this signal never depends on an external service.</param>
    /// <param name="fullTextCandidates">
    /// Full-text hits. Per §8.3 this is the second half of the degradation path, so a caller that has a usable
    /// semantic index passes an empty list here rather than mixing the two.
    /// </param>
    /// <param name="articleContentDate">The day being written. Nothing later may be cited.</param>
    /// <param name="limits">Ranking bounds; see <see cref="RetrievalLimits.Default"/>.</param>
    public static IReadOnlyList<RetrievedMaterial> Select(
        IEnumerable<RetrievalCandidate> semanticCandidates,
        IEnumerable<RetrievalCandidate> topicCandidates,
        IEnumerable<RetrievalCandidate> fullTextCandidates,
        ContentDate articleContentDate,
        RetrievalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(semanticCandidates);
        ArgumentNullException.ThrowIfNull(topicCandidates);
        ArgumentNullException.ThrowIfNull(fullTextCandidates);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        var best = new Dictionary<InputEntryId, RetrievalCandidate>();

        // Semantic first, then topic, then full text: the dictionary keeps the first candidate seen for an
        // entry unless a later one scores strictly higher, so signal priority is expressed by this ordering.
        foreach (var candidate in semanticCandidates
            .Concat(topicCandidates)
            .Concat(fullTextCandidates))
        {
            if (candidate is null || candidate.Entry is null)
            {
                continue;
            }

            if (!RecallPolicy.IsRecallable(candidate.Entry, articleContentDate))
            {
                continue;
            }

            var score = double.IsNaN(candidate.Score) ? 0 : Math.Clamp(candidate.Score, 0, 1);

            // A full-text hit must clear a higher bar than a topic hit: raw text overlap is noisy, and §8.3
            // asks for "高相关材料", not "material that shares a character with today".
            var floor = candidate.Signal == RetrievalSignal.FullText
                ? Math.Max(limits.MinimumRelevance, limits.MinimumLexicalScore)
                : limits.MinimumRelevance;

            if (score < floor)
            {
                continue;
            }

            var normalised = candidate with { Score = score };

            if (!best.TryGetValue(candidate.Entry.Id, out var existing) || score > existing.Score)
            {
                best[candidate.Entry.Id] = normalised;
            }
        }

        return best.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Entry.ContentDate)
            .ThenByDescending(candidate => candidate.Entry.CreatedAtUtc)

            // The retrieval query has no inherent order for equal-scoring entries, so the tie is broken by id
            // rather than by whatever the database happened to return. Same inputs, same prompt, every time.
            .ThenBy(candidate => candidate.Entry.Id.Value)
            .Take(limits.MaxMaterials)
            .Select(candidate => new RetrievedMaterial(
                candidate.Entry,
                candidate.Score,
                candidate.Reason,
                RecallPolicy.IsHistorical(candidate.Entry, articleContentDate)))
            .ToArray();
    }

    /// <summary>
    /// Builds the topic signal from the topics the day's own inputs are already filed under.
    /// <para>
    /// A shared primary topic is the strongest available evidence that a past entry is about the same thing;
    /// a shared secondary topic is real but weaker, so it ranks below a primary match instead of tying.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RetrievalCandidate> FromTopics(
        IEnumerable<InputEntry> dayInputs,
        IEnumerable<InputEntry> candidates,
        Func<TopicId, string?>? topicName = null)
    {
        ArgumentNullException.ThrowIfNull(dayInputs);
        ArgumentNullException.ThrowIfNull(candidates);

        var primary = new HashSet<TopicId>();
        var secondary = new HashSet<TopicId>();

        foreach (var entry in dayInputs)
        {
            if (entry.PrimaryTopicId is { IsEmpty: false } primaryTopicId)
            {
                primary.Add(primaryTopicId);
            }

            foreach (var topicId in entry.SecondaryTopicIds)
            {
                secondary.Add(topicId);
            }
        }

        // A topic that is primary on one input and secondary on another is primary for this purpose.
        secondary.ExceptWith(primary);

        if (primary.Count == 0 && secondary.Count == 0)
        {
            return [];
        }

        var results = new List<RetrievalCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.PrimaryTopicId is { IsEmpty: false } candidatePrimary && primary.Contains(candidatePrimary))
            {
                results.Add(new RetrievalCandidate(
                    candidate,
                    1.0,
                    Describe("共同的主要主题", candidatePrimary, topicName),
                    RetrievalSignal.Topic));
                continue;
            }

            var sharedSecondary = candidate.SecondaryTopicIds.FirstOrDefault(secondary.Contains);
            if (!sharedSecondary.IsEmpty)
            {
                results.Add(new RetrievalCandidate(
                    candidate,
                    0.7,
                    Describe("共同的次要主题", sharedSecondary, topicName),
                    RetrievalSignal.Topic));
            }
        }

        return results;
    }

    /// <summary>
    /// Builds the full-text signal. The query is the day's own text; a candidate that merely restates it does
    /// not score higher than one that genuinely shares subject matter, because Jaccard is symmetric.
    /// </summary>
    public static IReadOnlyList<RetrievalCandidate> FromFullText(
        string? dayText,
        IEnumerable<InputEntry> candidates,
        double minimumScore)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(dayText))
        {
            return [];
        }

        var query = TextSimilarity.Tokenize(dayText);
        if (query.Count == 0)
        {
            return [];
        }

        var results = new List<RetrievalCandidate>();

        foreach (var candidate in candidates)
        {
            var text = candidate.TranscriptForGeneration;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var score = TextSimilarity.Jaccard(query, TextSimilarity.Tokenize(text));
            if (score < minimumScore)
            {
                continue;
            }

            results.Add(new RetrievalCandidate(candidate, score, "内容与今天的输入相似", RetrievalSignal.FullText));
        }

        return results;
    }

    /// <summary>Concatenates the day's inputs in the order the prompt should read them (§8.4).</summary>
    public static string ComposeDayText(IEnumerable<InputEntry> dayInputs)
    {
        ArgumentNullException.ThrowIfNull(dayInputs);

        return string.Join(
            "\n",
            dayInputs
                .Where(entry => !string.IsNullOrWhiteSpace(entry.TranscriptForGeneration))
                .OrderBy(entry => entry.CreatedAtUtc)
                .Select(entry => entry.TranscriptForGeneration!.Trim()));
    }

    private static string Describe(string prefix, TopicId topicId, Func<TopicId, string?>? topicName)
    {
        var name = topicName?.Invoke(topicId);
        return string.IsNullOrWhiteSpace(name) ? prefix : $"{prefix}：{name.Trim()}";
    }
}
