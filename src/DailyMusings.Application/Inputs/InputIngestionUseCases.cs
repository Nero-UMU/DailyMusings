using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Inputs;

/// <summary>What the client gets back after capturing something.</summary>
public sealed record IngestResult(InputEntry Entry, bool WasAlreadyStored, ProcessingJob? TranscriptionJob);

/// <summary>Metadata a client sends alongside a capture.</summary>
public sealed record CaptureContext(
    DateTimeOffset CreatedAtUtc,
    int CreatedOffsetMinutes,
    string? ClientIdempotencyKey,
    DeviceId? DeviceId);

/// <summary>
/// Shared ingestion steps. Both entry points follow the same order because the order is the contract:
/// persist the audio, then record the entry, then enqueue the work — and only then answer the client
/// (docs/开发指导.md §8.2).
/// </summary>
public abstract class InputIngestionUseCaseBase
{
    protected InputIngestionUseCaseBase(
        IInputEntryRepository inputs,
        IJobRepository jobs,
        IUnitOfWork unitOfWork,
        IContentCalendarProvider calendars,
        IReflectionRepository reflections,
        IClock clock)
    {
        Inputs = inputs;
        Jobs = jobs;
        UnitOfWork = unitOfWork;
        Calendars = calendars;
        Reflections = reflections;
        Clock = clock;
    }

    protected IInputEntryRepository Inputs { get; }

    protected IJobRepository Jobs { get; }

    protected IUnitOfWork UnitOfWork { get; }

    protected IContentCalendarProvider Calendars { get; }

    /// <summary>Needed because a capture can invalidate an already-produced draft for its content day (§7).</summary>
    protected IReflectionRepository Reflections { get; }

    protected IClock Clock { get; }

    /// <summary>
    /// A replayed upload must not create a second entry. Returning the original is what lets the client treat a
    /// retry as success without having to know whether the first attempt landed (§9.2).
    /// </summary>
    protected async Task<InputEntry?> FindReplayAsync(string? clientIdempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientIdempotencyKey))
        {
            return null;
        }

        return await Inputs.FindByClientKeyAsync(clientIdempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the entry and its transcription job in one transaction. Committing them together is what prevents
    /// an entry that can never be transcribed because the process died between the two writes.
    /// <para>
    /// The same transaction also invalidates the day's draft if it had already been produced. That belongs here,
    /// next to the write that caused it: §7's staleness rule is about material arriving after the draft exists,
    /// and doing it anywhere else would leave a window in which a new capture is stored but the draft still looks
    /// finished.
    /// </para>
    /// </summary>
    protected async Task<ProcessingJob?> PersistAsync(
        InputEntry entry,
        bool enqueueTranscription,
        CancellationToken cancellationToken)
    {
        await using var transaction = await UnitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        await Inputs.AddAsync(entry, cancellationToken).ConfigureAwait(false);

        ProcessingJob? job = null;
        if (enqueueTranscription)
        {
            job = ProcessingJob.Create(
                JobId.New(),
                JobType.Transcription,
                entry.Id.ToString(),
                IdempotencyKeys.Transcription(entry.Id),
                Clock.UtcNow);

            await Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        }

        await MarkDayStaleIfNeededAsync(entry.ContentDate, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>
    /// §7's two staleness paths, which are really one rule: material whose own content day already has a draft
    /// invalidates that draft. Path ① is a same-day capture arriving after the nightly run; path ② is yesterday's
    /// capture arriving today. Because the rule keys off the entry's immutable content day (A.5), neither path
    /// needs a special case.
    /// </summary>
    private async Task MarkDayStaleIfNeededAsync(ContentDate contentDate, CancellationToken cancellationToken)
    {
        var reflection = await Reflections
            .FindByContentDateAsync(contentDate, cancellationToken)
            .ConfigureAwait(false);

        if (reflection is null || !GenerationRules.ShouldMarkStale(reflection.Status))
        {
            return;
        }

        reflection.MarkStaleByLateInput(Clock.UtcNow);
        await Reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The content day is always derived from the capture instant in the content time zone — never from the
    /// device's own offset, and never from when the server received it (§7, decision A.4).
    /// </summary>
    protected async Task<ContentDate> ResolveContentDateAsync(
        CaptureContext context,
        CancellationToken cancellationToken)
    {
        var calendar = await Calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        return calendar.ContentDateOf(context.CreatedAtUtc);
    }
}

/// <summary>Records a voice capture. The blob is durable before this method returns.</summary>
public sealed class IngestVoiceInputUseCase : InputIngestionUseCaseBase
{
    private readonly IAudioStore _audio;
    private readonly ITranscriptionRunner _transcription;
    private readonly IInstanceSettingsProvider _instanceSettings;

    public IngestVoiceInputUseCase(
        IInputEntryRepository inputs,
        IJobRepository jobs,
        IUnitOfWork unitOfWork,
        IContentCalendarProvider calendars,
        IReflectionRepository reflections,
        IAudioStore audio,
        ITranscriptionRunner transcription,
        IInstanceSettingsProvider instanceSettings,
        IClock clock)
        : base(inputs, jobs, unitOfWork, calendars, reflections, clock)
    {
        _audio = audio;
        _transcription = transcription;
        _instanceSettings = instanceSettings;
    }

    /// <param name="transcribeNow">
    /// Attempt the transcription inline and return the text with the upload. §8.2 wants the client to get its
    /// transcript without polling; the queue still owns the work, so this is best-effort by construction.
    /// </param>
    public async Task<IngestResult> ExecuteAsync(
        Stream audio,
        string? contentType,
        TimeSpan? duration,
        CaptureContext context,
        CancellationToken cancellationToken,
        bool transcribeNow = false)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (await FindReplayAsync(context.ClientIdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            // A replayed upload is answered with what was already stored, transcript and all. Running the
            // transcription again would be work for nothing, and could rewrite a transcript the user already
            // corrected — §9.2 makes a retry a *confirmation*, not a second capture.
            return new IngestResult(existing, WasAlreadyStored: true, null);
        }

        var contentDate = await ResolveContentDateAsync(context, cancellationToken).ConfigureAwait(false);
        var inputId = InputEntryId.New();

        // Step 1 of §8.2: the audio reaches durable storage before anything else happens. If this throws, the
        // client is correctly told the upload failed and will retry with the same idempotency key.
        var stored = await _audio
            .SaveAsync(inputId, contentDate, audio, contentType, cancellationToken)
            .ConfigureAwait(false);

        var entry = InputEntry.CreateVoice(
            inputId,
            context.CreatedAtUtc,
            context.CreatedOffsetMinutes,
            contentDate,
            stored.RelativePath,
            duration,
            stored.ContentType,
            context.ClientIdempotencyKey,
            context.DeviceId);

        // Steps 2–3: record the entry and queue the transcription together.
        var job = await PersistAsync(entry, enqueueTranscription: true, cancellationToken).ConfigureAwait(false);

        var result = new IngestResult(entry, WasAlreadyStored: false, job);

        if (!transcribeNow)
        {
            return result;
        }

        // Step 4, brought forward: try the transcription now so the answer can carry the text. Everything about
        // this is best-effort and it happens strictly after the transaction committed, so a slow, broken or
        // unconfigured model cannot turn a successful upload into a failed one (§20: 不因模型暂时失败而丢失输入).
        return await TryTranscribeInlineAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestResult> TryTranscribeInlineAsync(
        IngestResult result,
        CancellationToken cancellationToken)
    {
        var settings = await _instanceSettings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (settings.InlineTranscriptionTimeoutSeconds <= 0)
        {
            // Switched off: pure queue behaviour, which is what an operator with a slow endpoint asked for.
            return result;
        }

        var window = settings.InlineTranscriptionTimeout;

        // Keep the queue off this job while the inline attempt owns it. Without this the executor can claim the
        // very same transcription two seconds in and call the model a second time for one recording — and the
        // two writers would race over the entry's status.
        var deferred = await DeferJobAsync(result.TranscriptionJob, Clock.UtcNow + window, cancellationToken)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);

        try
        {
            var refreshed = await _transcription
                .RunAsync(result.Entry.Id, jobPayloadJson: null, timeout.Token)
                .ConfigureAwait(false);

            if (refreshed is not null && result.TranscriptionJob is { } completed)
            {
                // Finished here, so the queue entry is finished here too: leaving it pending would have the
                // executor pick up work that is already done.
                await CompleteJobAsync(completed, cancellationToken).ConfigureAwait(false);
            }

            return refreshed is null ? result : result with { Entry = refreshed };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The job is already queued and the audio is already stored, so the only correct thing to do here is
            // to say nothing: the upload succeeded, and the transcript arrives by the normal path moments later.
            // A cancellation caused by *our own* timeout lands here too — that is the expected outcome of a slow
            // model, not a failure of the capture.
            _ = exception;

            if (deferred)
            {
                // Nothing usable came out of the attempt, so hand the work straight back to the queue rather
                // than making the user wait out a deferral that no longer protects anything. The retry, the
                // backoff and the terminal-failure mail stay the executor's business, exactly as before.
                await MakeDueAsync(result.TranscriptionJob!, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away mid-wait. The capture is already durable, so nothing is lost — but the queue
            // must be released now rather than at the end of a deferral nobody is waiting behind any more. The
            // bookkeeping write deliberately does not use the cancelled token.
            if (deferred)
            {
                await MakeDueAsync(result.TranscriptionJob!, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Pushes the job's due time out for the duration of an inline attempt. Returns false when the job was
    /// already claimed by the executor, in which case the inline path is the redundant one and says nothing.
    /// </summary>
    private async Task<bool> DeferJobAsync(
        ProcessingJob? job,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            return false;
        }

        // Re-read: the executor may have claimed it between the commit and now, and writing a stale copy back
        // would revert a running job to pending — which is how the same work ends up running twice.
        var current = await Jobs.FindByIdAsync(job.Id, cancellationToken).ConfigureAwait(false);

        if (current is null || current.Status != JobStatus.Pending)
        {
            return false;
        }

        current.Defer(until);
        await Jobs.UpdateAsync(current, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Marks the job finished after the inline attempt did the work, the same way the executor would: claim it
    /// first, then succeed it. Claiming is not decoration — a job cannot move from pending to succeeded without
    /// having been started, and the exclusive claim is also what stops this from overwriting the executor's own
    /// record if it got there first.
    /// </summary>
    private async Task CompleteJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        if (!await Jobs.TryClaimAsync(job.Id, Clock.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var current = await Jobs.FindByIdAsync(job.Id, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            return;
        }

        current.Succeed(Clock.UtcNow);
        await Jobs.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes the job due again, unless the executor has taken it over in the meantime.</summary>
    private async Task MakeDueAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        var current = await Jobs.FindByIdAsync(job.Id, cancellationToken).ConfigureAwait(false);

        if (current is null || current.Status != JobStatus.Pending)
        {
            return;
        }

        current.Defer(Clock.UtcNow);
        await Jobs.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Records a typed note. There is nothing to transcribe, so no job is queued — but the note is filed under the
/// user's topics straight away, because §8.2's recognition step exists to make material findable and a typed note
/// is material from the moment it arrives.
/// </summary>
public sealed class IngestTextInputUseCase : InputIngestionUseCaseBase
{
    private readonly Topics.AssignTopicsAutomaticallyUseCase _assignTopics;
    private readonly Embeddings.EnsureEmbeddingIndexedUseCase _ensureIndexed;

    public IngestTextInputUseCase(
        IInputEntryRepository inputs,
        IJobRepository jobs,
        IUnitOfWork unitOfWork,
        IContentCalendarProvider calendars,
        IReflectionRepository reflections,
        Topics.AssignTopicsAutomaticallyUseCase assignTopics,
        Embeddings.EnsureEmbeddingIndexedUseCase ensureIndexed,
        IClock clock)
        : base(inputs, jobs, unitOfWork, calendars, reflections, clock)
    {
        _assignTopics = assignTopics;
        _ensureIndexed = ensureIndexed;
    }

    public async Task<IngestResult> ExecuteAsync(
        string text,
        CaptureContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (await FindReplayAsync(context.ClientIdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return new IngestResult(existing, WasAlreadyStored: true, null);
        }

        var contentDate = await ResolveContentDateAsync(context, cancellationToken).ConfigureAwait(false);

        var entry = InputEntry.CreateText(
            InputEntryId.New(),
            context.CreatedAtUtc,
            context.CreatedOffsetMinutes,
            contentDate,
            text,
            context.ClientIdempotencyKey,
            context.DeviceId);

        var job = await PersistAsync(entry, enqueueTranscription: false, cancellationToken).ConfigureAwait(false);

        try
        {
            await _assignTopics.ExecuteAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Filing is a convenience: a capture the user typed must never be lost because topic matching failed.
            // The caller sees the entry as captured, and the note simply stays unfiled until they file it.
            _ = exception;
        }

        // The vector for this text is what lets a later day retrieve it semantically (§8.3), so the same capture
        // that produced the text queues the embedding.
        await _ensureIndexed
            .ExecuteAsync(entry.Id, entry.TranscriptForGeneration, cancellationToken)
            .ConfigureAwait(false);

        return new IngestResult(entry, WasAlreadyStored: false, job);
    }
}
