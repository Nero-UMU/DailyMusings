using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Inputs;

/// <summary>
/// An entry together with the state of its transcription. The client needs both at once to render "uploaded,
/// transcribing, done, failed" without a second round trip (§9.1).
/// </summary>
public sealed record InputStatusView(InputEntry Entry, ProcessingJob? TranscriptionJob)
{
    public bool IsAwaitingTranscription =>
        TranscriptionJob is { IsTerminal: false } ||
        Entry.TranscriptionStatus is TranscriptionStatus.Pending or TranscriptionStatus.InProgress;

    /// <summary>Stable, content-free reason a client can show next to a failed capture (§9.2).</summary>
    public string? FailureCode => TranscriptionJob?.ErrorCode ?? Entry.TranscriptionErrorCode;
}

/// <summary>One page of captures plus the total a pager needs (docs/开发指导.md §13).</summary>
public sealed record InputPage(IReadOnlyList<InputStatusView> Items, int Total, int Page, int PageSize)
{
    public static InputPage Single(IReadOnlyList<InputStatusView> items) =>
        new(items, items.Count, 1, Math.Max(items.Count, 1));
}

/// <summary>Reads entries for a day, or the most recent ones.</summary>
public sealed class ListInputsUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IJobRepository _jobs;

    public ListInputsUseCase(IInputEntryRepository inputs, IJobRepository jobs)
    {
        _inputs = inputs;
        _jobs = jobs;
    }

    public async Task<IReadOnlyList<InputStatusView>> ExecuteAsync(
        ContentDate? contentDate,
        int recentLimit,
        CancellationToken cancellationToken)
    {
        var entries = contentDate is { } day
            ? await _inputs.ListByContentDateAsync(day, cancellationToken).ConfigureAwait(false)
            : await _inputs.ListRecentAsync(recentLimit, cancellationToken).ConfigureAwait(false);

        return await ToViewsAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One page for the admin content list.
    /// <para>
    /// A day query stays exactly what it was — the phone's timeline asks for one day and shows all of it, in
    /// capture order, and paging that would only be a way of hiding rows from a screen that has nothing to
    /// scroll. The undated query is the archive view, and that one is newest-first and paged.
    /// </para>
    /// </summary>
    /// <param name="includeDeleted">
    /// Whether soft-deleted tombstones are listed. The retention sweep leaves them behind (they hold a
    /// historical article's provenance), and an operator asking "what happened to that entry" needs to see
    /// them; the phone never asks, because a capture the user deleted must not come back.
    /// </param>
    public async Task<InputPage> ExecutePageAsync(
        ContentDate? contentDate,
        int page,
        int pageSize,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        if (contentDate is { } day)
        {
            var dayEntries = await _inputs.ListByContentDateAsync(day, cancellationToken).ConfigureAwait(false);

            return InputPage.Single(await ToViewsAsync(dayEntries, cancellationToken).ConfigureAwait(false));
        }

        var offset = (page - 1) * pageSize;
        var entries = await _inputs
            .ListPageAsync(offset, pageSize, includeDeleted, cancellationToken)
            .ConfigureAwait(false);

        var total = await _inputs.CountAsync(includeDeleted, cancellationToken).ConfigureAwait(false);

        return new InputPage(
            await ToViewsAsync(entries, cancellationToken).ConfigureAwait(false),
            total,
            page,
            pageSize);
    }

    private async Task<IReadOnlyList<InputStatusView>> ToViewsAsync(
        IReadOnlyList<InputEntry> entries,
        CancellationToken cancellationToken)
    {
        var views = new List<InputStatusView>(entries.Count);

        foreach (var entry in entries)
        {
            views.Add(new InputStatusView(entry, await FindJobAsync(entry, cancellationToken).ConfigureAwait(false)));
        }

        return views;
    }

    private Task<ProcessingJob?> FindJobAsync(InputEntry entry, CancellationToken cancellationToken) =>
        _jobs.FindByTypeAndTargetAsync(JobType.Transcription, entry.Id.ToString(), cancellationToken);
}

public sealed class GetInputUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IJobRepository _jobs;

    public GetInputUseCase(IInputEntryRepository inputs, IJobRepository jobs)
    {
        _inputs = inputs;
        _jobs = jobs;
    }

    public async Task<InputStatusView> ExecuteAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        // A deleted entry is reported as unknown rather than returned with a flag: the client cannot do anything
        // with it, and a 404 is the honest answer to "show me this capture".
        if (entry.IsDeleted)
        {
            throw new DomainException("input.unknown", $"No input with id {id}.");
        }

        var job = await _jobs
            .FindByTypeAndTargetAsync(JobType.Transcription, entry.Id.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return new InputStatusView(entry, job);
    }
}

/// <summary>
/// Stores the user's correction to a transcript. The original is never overwritten (§6.1), and — per §7 —
/// revising a past day's transcript must not make that day's reflection regenerable. This use case therefore
/// touches only the entry; regeneration eligibility stays where it belongs, in the reflection rules.
/// <para>
/// It does queue a re-embedding, because §7 also says 修改后的内容可参与未来主题检索: a stored vector describes the
/// wording the user just replaced, and semantic retrieval would otherwise keep matching against it.
/// </para>
/// </summary>
public sealed class ReviseTranscriptUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly Embeddings.EnsureEmbeddingIndexedUseCase _ensureIndexed;

    public ReviseTranscriptUseCase(
        IInputEntryRepository inputs,
        Embeddings.EnsureEmbeddingIndexedUseCase ensureIndexed)
    {
        _inputs = inputs;
        _ensureIndexed = ensureIndexed;
    }

    public async Task<InputEntry> ExecuteAsync(
        InputEntryId id,
        string? revisedTranscript,
        CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        entry.ReviseTranscript(revisedTranscript);
        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

        // Keyed by the text itself, so re-saving the same revision is still a no-op while a real change is not.
        await _ensureIndexed
            .ExecuteAsync(entry.Id, entry.TranscriptForGeneration, cancellationToken)
            .ConfigureAwait(false);

        return entry;
    }
}

/// <summary>
/// Deletes only the audio, keeping the entry and its transcripts (§17.1). The blob is removed first: a dangling
/// path in the database is recoverable, whereas a blob with no row referencing it is invisible forever.
/// </summary>
public sealed class DeleteInputAudioUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly IClock _clock;

    public DeleteInputAudioUseCase(IInputEntryRepository inputs, IAudioStore audio, IClock clock)
    {
        _inputs = inputs;
        _audio = audio;
        _clock = clock;
    }

    public async Task ExecuteAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        var path = entry.DeleteAudio(_clock.UtcNow);
        if (path is not null)
        {
            await _audio.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Permanently deletes a user-selected entry, taking its audio and article source links with it. This is separate
/// from retention cleanup, which must keep an empty tombstone for provenance after content expires automatically.
/// </summary>
public sealed class DeleteInputUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;

    public DeleteInputUseCase(IInputEntryRepository inputs, IAudioStore audio)
    {
        _inputs = inputs;
        _audio = audio;
    }

    public async Task ExecuteAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        if (entry.IsDeleted)
        {
            throw new DomainException("input.unknown", $"No input with id {id}.");
        }

        var audioPath = entry.AudioPath;

        // The row goes first, and the order matters. The row is the record of truth and the audio is a blob it
        // points at, so the only drift worth designing against is a live row whose blob is already gone. An
        // orphan blob left behind by a failed cleanup is inert, invisible and reclaimable; a row pointing at
        // nothing is neither. The audio store is best-effort by contract (it swallows IO failures), so a
        // completed delete can never be turned into a failed request by this step.
        await _inputs.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (audioPath is { } path)
        {
            await _audio.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }

        // Any queued transcription is deliberately left in place: the handler treats "the entry is gone" as
        // success, which is cheaper and safer than trying to cancel a job that may already be running.
    }
}

/// <summary>
/// Puts a failed transcription back in the queue (§13 POST /api/inputs/{id}/retry-transcription). The entry's
/// audio is still there — §20 forbids losing input because a model was unavailable.
/// </summary>
public sealed class RetryTranscriptionUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IJobRepository _jobs;
    private readonly IClock _clock;

    public RetryTranscriptionUseCase(IInputEntryRepository inputs, IJobRepository jobs, IClock clock)
    {
        _inputs = inputs;
        _jobs = jobs;
        _clock = clock;
    }

    public async Task<InputStatusView> ExecuteAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        if (entry.SourceType != InputSourceType.Voice)
        {
            throw new DomainException("input.retry.not_voice", "Only voice entries have a transcription to retry.");
        }

        if (!entry.HasAudio)
        {
            throw new DomainException("input.retry.audio_missing", "The audio for this entry is no longer available.");
        }

        entry.RetryTranscription();

        var job = await _jobs
            .FindByTypeAndTargetAsync(JobType.Transcription, entry.Id.ToString(), cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            // A missing job is possible if the original enqueue failed; recreating it keeps the entry recoverable.
            job = ProcessingJob.Create(
                JobId.New(),
                JobType.Transcription,
                entry.Id.ToString(),
                IdempotencyKeys.Transcription(entry.Id),
                _clock.UtcNow);

            await _jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            job.Requeue(_clock.UtcNow);
            await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        }

        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

        return new InputStatusView(entry, job);
    }
}
