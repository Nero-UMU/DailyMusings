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
/// </summary>
public sealed class ReviseTranscriptUseCase
{
    private readonly IInputEntryRepository _inputs;

    public ReviseTranscriptUseCase(IInputEntryRepository inputs) => _inputs = inputs;

    public async Task<InputEntry> ExecuteAsync(
        InputEntryId id,
        string? revisedTranscript,
        CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        entry.ReviseTranscript(revisedTranscript);
        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

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

/// <summary>Deletes the whole entry, taking its audio with it.</summary>
public sealed class DeleteInputUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly IClock _clock;

    public DeleteInputUseCase(IInputEntryRepository inputs, IAudioStore audio, IClock clock)
    {
        _inputs = inputs;
        _audio = audio;
        _clock = clock;
    }

    public async Task ExecuteAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("input.unknown", $"No input with id {id}.");

        var path = entry.Delete(_clock.UtcNow);
        if (path is not null)
        {
            await _audio.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

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
