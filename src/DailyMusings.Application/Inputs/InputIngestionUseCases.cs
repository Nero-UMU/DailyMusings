using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
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
        IClock clock)
    {
        Inputs = inputs;
        Jobs = jobs;
        UnitOfWork = unitOfWork;
        Calendars = calendars;
        Clock = clock;
    }

    protected IInputEntryRepository Inputs { get; }

    protected IJobRepository Jobs { get; }

    protected IUnitOfWork UnitOfWork { get; }

    protected IContentCalendarProvider Calendars { get; }

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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
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
        IAudioStore audio,
        IClock clock)
        : base(inputs, jobs, unitOfWork, calendars, clock)
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

/// <summary>Records a typed note. There is nothing to transcribe, so no job is queued.</summary>
public sealed class IngestTextInputUseCase : InputIngestionUseCaseBase
{
    public IngestTextInputUseCase(
        IInputEntryRepository inputs,
        IJobRepository jobs,
        IUnitOfWork unitOfWork,
        IContentCalendarProvider calendars,
        IClock clock)
        : base(inputs, jobs, unitOfWork, calendars, clock)
    {
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

        return new IngestResult(entry, WasAlreadyStored: false, job);
    }
}
