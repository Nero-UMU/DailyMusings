using System.Security.Cryptography;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;

namespace DailyMusings.Application.Embeddings;

/// <summary>
/// Identifies the exact text a vector was produced from.
/// <para>
/// An edited transcript must be re-embedded — a vector for the old wording would keep surfacing the entry for
/// searches that its current text no longer answers — so the index records this alongside the vector and the
/// index job uses it to skip work that is still current.
/// </para>
/// </summary>
public static class EmbeddingTextHash
{
    public static string Compute(string? text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));
}

/// <summary>
/// Queues one entry for embedding, if embeddings are configured and there is text to embed.
/// <para>
/// Two callers need this — a transcription that just produced text, and a revision that just replaced it — and
/// they must agree on when a re-embed is a no-op. Putting the key derivation in one place is what makes "the same
/// text is never embedded twice" and "a changed text always is" the same rule rather than two.
/// </para>
/// </summary>
public sealed class EnsureEmbeddingIndexedUseCase
{
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly Application.Jobs.JobEnqueuer _jobs;

    public EnsureEmbeddingIndexedUseCase(
        IEmbeddingSettingsProvider settings,
        Application.Jobs.JobEnqueuer jobs)
    {
        _settings = settings;
        _jobs = jobs;
    }

    public async Task<bool> ExecuteAsync(
        InputEntryId inputId,
        string? text,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        // Nothing is queued while embeddings are switched off: an index job for a disabled endpoint could only
        // fail, and §3.1 keeps the endpoint optional rather than required.
        if (!settings.Enabled || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var configVersion = settings.ResolveConfigVersion().Fingerprint;

        await _jobs.EnsureAsync(
            JobType.EmbeddingIndex,
            inputId.ToString(),
            IdempotencyKeys.EmbeddingIndex(inputId, configVersion, EmbeddingTextHash.Compute(text)),
            payload: null,
            requeueFailed: false,
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Whether semantic history retrieval can be used right now (docs/开发指导.md §8.3, decision A.8).
/// <para>
/// This is a queryable state rather than a per-search probe, because two things depend on being able to ask it
/// without touching the embedding service: the client's "语义检索重建中" notice, and §16's rule that an
/// unavailable external service must not make the basic health check fail.
/// </para>
/// </summary>
public sealed class GetSemanticSearchStateUseCase
{
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly IEmbeddingIndexState _indexState;

    public GetSemanticSearchStateUseCase(IEmbeddingSettingsProvider settings, IEmbeddingIndexState indexState)
    {
        _settings = settings;
        _indexState = indexState;
    }

    public async Task<SemanticSearchState> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return SemanticSearchState.Disabled;
        }

        var configured = settings.ResolveConfigVersion().Fingerprint;
        var indexed = await _indexState.GetIndexedVersionAsync(cancellationToken).ConfigureAwait(false);

        return new SemanticSearchState(
            Enabled: true,
            Available: string.Equals(indexed, configured, StringComparison.Ordinal),
            ConfiguredVersion: configured,
            IndexedVersion: indexed);
    }
}

/// <summary>
/// Embeds one input's text and stores the vector (§8.2 step 5: 成功后保存原始转写，识别主题并生成 Embedding).
/// </summary>
public sealed class EmbedInputUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly IEmbeddingClient _client;
    private readonly IEmbeddingIndexRepository _index;
    private readonly IClock _clock;

    public EmbedInputUseCase(
        IInputEntryRepository inputs,
        IEmbeddingSettingsProvider settings,
        IEmbeddingClient client,
        IEmbeddingIndexRepository index,
        IClock clock)
    {
        _inputs = inputs;
        _settings = settings;
        _client = client;
        _index = index;
        _clock = clock;
    }

    /// <returns><c>false</c> when there was nothing to embed, which is a normal outcome rather than a failure.</returns>
    public async Task<bool> ExecuteAsync(InputEntryId inputId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return false;
        }

        var entry = await _inputs.FindByIdAsync(inputId, cancellationToken).ConfigureAwait(false);
        var text = entry is null || entry.IsDeleted ? null : entry.TranscriptForGeneration;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var config = settings.ResolveConfigVersion();

        var results = await _client
            .EmbedAsync(new EmbeddingRequest([text], settings.Model, settings.Dimensions), cancellationToken)
            .ConfigureAwait(false);

        var vector = results.FirstOrDefault();
        if (vector is null || vector.Vector.Length == 0)
        {
            throw new TransientExternalFailureException(
                "embedding.empty_response",
                "The embedding endpoint returned no vector.");
        }

        await _index
            .UpsertAsync(
                inputId,
                config.Fingerprint,
                settings.Model,
                vector.Vector.Length,
                vector.Vector,
                EmbeddingTextHash.Compute(text),
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}

/// <summary>What one batch of a rebuild achieved.</summary>
public sealed record EmbeddingBatchResult(bool Complete, int Indexed, int Remaining);

/// <summary>
/// Rebuilds the index for a configuration fingerprint (§8.3, decision A.8).
/// <para>
/// Batched on purpose. The job runs one batch per attempt and reports "more to do" through
/// <c>JobOutcome.Continue</c>, which returns it to the queue without spending a retry — otherwise a large
/// archive would exhaust §14's three attempts part way through and leave the index permanently incomplete.
/// </para>
/// <para>
/// The switch to the new index is one write, and it happens only when nothing is left pending, so the previous
/// index stays readable for the whole rebuild and the degraded path is never the only available one.
/// </para>
/// </summary>
public sealed class RebuildEmbeddingIndexUseCase
{
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly IEmbeddingClient _client;
    private readonly IEmbeddingIndexRepository _index;
    private readonly IEmbeddingIndexState _indexState;
    private readonly IClock _clock;

    public RebuildEmbeddingIndexUseCase(
        IEmbeddingSettingsProvider settings,
        IEmbeddingClient client,
        IEmbeddingIndexRepository index,
        IEmbeddingIndexState indexState,
        IClock clock)
    {
        _settings = settings;
        _client = client;
        _index = index;
        _indexState = indexState;
        _clock = clock;
    }

    public async Task<EmbeddingBatchResult> ExecuteAsync(string configVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configVersion);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            // Nothing to build: the endpoint is switched off, and §8.3's degraded path is the intended
            // behaviour rather than a failure to report.
            return new EmbeddingBatchResult(Complete: true, Indexed: 0, Remaining: 0);
        }

        var pending = await _index
            .ListPendingAsync(configVersion, settings.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var indexed = 0;

        if (pending.Count > 0)
        {
            var results = await _client
                .EmbedAsync(
                    new EmbeddingRequest(
                        pending.Select(item => item.Text).ToArray(),
                        settings.Model,
                        settings.Dimensions),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var result in results)
            {
                if (result.Index < 0 || result.Index >= pending.Count || result.Vector.Length == 0)
                {
                    continue;
                }

                var item = pending[result.Index];
                await _index
                    .UpsertAsync(
                        item.InputId,
                        configVersion,
                        settings.Model,
                        result.Vector.Length,
                        result.Vector,
                        EmbeddingTextHash.Compute(item.Text),
                        _clock.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false);

                indexed++;
            }
        }

        var remaining = await _index
            .CountPendingAsync(configVersion, cancellationToken)
            .ConfigureAwait(false);

        if (remaining > 0)
        {
            return new EmbeddingBatchResult(Complete: false, indexed, Remaining: remaining);
        }

        await _indexState.MarkIndexedAsync(configVersion, cancellationToken).ConfigureAwait(false);

        // Only now are the superseded rows removed: A.8 keeps a single generation of the index, but the old one
        // has to survive until the new one is complete or retrieval would have nothing to fall back on.
        await _index.DeleteVersionsOtherThanAsync(configVersion, cancellationToken).ConfigureAwait(false);

        return new EmbeddingBatchResult(Complete: true, indexed, Remaining: 0);
    }
}
