using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Reflections;

/// <summary>Which path actually produced the material list, so the client can say so honestly (§8.3).</summary>
public enum RetrievalMode
{
    /// <summary>Embedding similarity was used.</summary>
    Semantic = 0,

    /// <summary>Fell back to topic tags and full-text search, per §8.3's degradation rule.</summary>
    Degraded = 1,
}

public sealed record RetrievalOutcome(
    IReadOnlyList<RetrievedMaterial> Materials,
    RetrievalMode Mode,
    SemanticSearchState SemanticSearch);

/// <summary>
/// Chooses the past material a day's reflection may cite (docs/开发指导.md §8.3).
/// <para>
/// Two decisions are worth naming. First, the query for semantic retrieval is the day's own inputs' vectors
/// rather than a fresh embedding call: the same index already holds them, so retrieval needs no second network
/// round trip and has no failure mode of its own at generation time. Second, degradation is a property of the
/// whole run, not of a single search — when the index is not usable, full text takes its place rather than
/// supplementing it, because §8.3 describes the fallback as a substitute.
/// </para>
/// </summary>
public sealed class HistoryRetrievalUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly ITopicRepository _topics;
    private readonly IEmbeddingIndexRepository _index;
    private readonly IEmbeddingSettingsProvider _embeddingSettings;
    private readonly IRetrievalSettingsProvider _retrievalSettings;
    private readonly Embeddings.GetSemanticSearchStateUseCase _semanticState;

    public HistoryRetrievalUseCase(
        IInputEntryRepository inputs,
        ITopicRepository topics,
        IEmbeddingIndexRepository index,
        IEmbeddingSettingsProvider embeddingSettings,
        IRetrievalSettingsProvider retrievalSettings,
        Embeddings.GetSemanticSearchStateUseCase semanticState)
    {
        _inputs = inputs;
        _topics = topics;
        _index = index;
        _embeddingSettings = embeddingSettings;
        _retrievalSettings = retrievalSettings;
        _semanticState = semanticState;
    }

    public async Task<RetrievalOutcome> ExecuteAsync(
        ContentDate articleContentDate,
        IReadOnlyList<InputEntry> dayInputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dayInputs);

        var settings = await _retrievalSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var limits = settings.ToLimits();
        var semantic = await GetSemanticSearchStateAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await _inputs
            .ListRecallCandidatesAsync(articleContentDate, settings.CandidateScanLimit, cancellationToken)
            .ConfigureAwait(false);

        // The day's own entries are the prompt's *subject*, not its history: they are already passed to the model
        // as the day's material. Leaving them in the pool would offer the same text twice and, for full text,
        // hand back a perfect score for matching an entry against itself.
        var dayIds = dayInputs.Select(entry => entry.Id).ToHashSet();
        var pool = candidates.Where(candidate => !dayIds.Contains(candidate.Id)).ToArray();

        var topicNames = await BuildTopicNameMapAsync(cancellationToken).ConfigureAwait(false);

        var topicCandidates = RetrievalPolicy.FromTopics(dayInputs, pool, topicId =>
            topicNames.TryGetValue(topicId, out var name) ? name : null);

        if (semantic.Available)
        {
            var semanticCandidates = await BuildSemanticCandidatesAsync(
                dayInputs,
                pool,
                articleContentDate,
                settings.CandidateScanLimit,
                cancellationToken).ConfigureAwait(false);

            if (semanticCandidates.Count > 0)
            {
                return new RetrievalOutcome(
                    RetrievalPolicy.Select(semanticCandidates, topicCandidates, [], articleContentDate, limits),
                    RetrievalMode.Semantic,
                    semantic);
            }
        }

        var fullTextCandidates = RetrievalPolicy.FromFullText(
            RetrievalPolicy.ComposeDayText(dayInputs),
            pool,
            limits.MinimumLexicalScore);

        return new RetrievalOutcome(
            RetrievalPolicy.Select([], topicCandidates, fullTextCandidates, articleContentDate, limits),
            RetrievalMode.Degraded,
            semantic);
    }

    /// <summary>
    /// Whether semantic retrieval can be used right now (decision A.8). A stale index degrades rather than
    /// failing: the user gets results, and the state travels with them so the client can show "语义检索重建中".
    /// </summary>
    public Task<SemanticSearchState> GetSemanticSearchStateAsync(CancellationToken cancellationToken) =>
        _semanticState.ExecuteAsync(cancellationToken);

    private async Task<IReadOnlyList<RetrievalCandidate>> BuildSemanticCandidatesAsync(
        IReadOnlyList<InputEntry> dayInputs,
        IReadOnlyList<InputEntry> candidates,
        ContentDate articleContentDate,
        int scanLimit,
        CancellationToken cancellationToken)
    {
        var settings = await _embeddingSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var configVersion = settings.ResolveConfigVersion().Fingerprint;

        var stored = await _index
            .ListVectorsAsync(configVersion, articleContentDate, scanLimit, cancellationToken)
            .ConfigureAwait(false);

        if (stored.Count == 0)
        {
            return [];
        }

        var dayIds = dayInputs.Select(entry => entry.Id).ToHashSet();
        var byId = stored.ToDictionary(entry => entry.InputId);

        // The query is the day's own vectors: no extra embedding call, and no new failure mode at retrieval
        // time. Without them there is nothing to compare against, and the caller degrades.
        var queries = stored.Where(entry => dayIds.Contains(entry.InputId)).Select(entry => entry.Vector).ToArray();
        if (queries.Length == 0)
        {
            return [];
        }

        var results = new List<RetrievalCandidate>();

        foreach (var candidate in candidates)
        {
            if (dayIds.Contains(candidate.Id) || !byId.TryGetValue(candidate.Id, out var vector))
            {
                continue;
            }

            var best = 0.0;
            foreach (var query in queries)
            {
                best = Math.Max(best, VectorMath.CosineSimilarity(query, vector.Vector));
            }

            if (best > 0)
            {
                results.Add(new RetrievalCandidate(candidate, best, "语义相似", RetrievalSignal.Semantic));
            }
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<TopicId, string>> BuildTopicNameMapAsync(CancellationToken cancellationToken)
    {
        // Merged topics are included: a citation's reason should still name the topic the material was filed
        // under when it was filed, and A.9 keeps that row precisely so history stays readable.
        var topics = await _topics.ListAsync(includeMerged: true, cancellationToken).ConfigureAwait(false);
        return topics.ToDictionary(topic => topic.Id, topic => topic.Name);
    }
}
