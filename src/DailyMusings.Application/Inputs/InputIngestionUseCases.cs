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

    public IngestVoiceInputUseCase(
        IInputEntryRepository inputs,
        IJobRepository jobs,
        IUnitOfWork unitOfWork,
        IContentCalendarProvider calendars,
        IReflectionRepository reflections,
        IAudioStore audio,
        IClock clock)
        : base(inputs, jobs, unitOfWork, calendars, reflections, clock)
    {
        _audio = audio;
    }

    public async Task<IngestResult> ExecuteAsync(
        Stream audio,
        string? contentType,
        TimeSpan? duration,
        CaptureContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (await FindReplayAsync(context.ClientIdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
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

        return new IngestResult(entry, WasAlreadyStored: false, job);
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
