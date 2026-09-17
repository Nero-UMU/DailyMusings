using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Topics;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>What a handler reports back when it did not throw.</summary>
public enum JobOutcome
{
    /// <summary>The work was done.</summary>
    Completed = 0,

    /// <summary>
    /// There was nothing left to do — the target was deleted, already handled, or otherwise no longer applies.
    /// Counted as success, because failing would only produce a pointless retry storm.
    /// </summary>
    Skipped = 1,

    /// <summary>
    /// The work made progress and has more to do. The job returns to the queue without being counted as a failed
    /// attempt, which is what lets §8.3's batched index rebuild fit inside §14's bounded retry budget.
    /// </summary>
    Continue = 2,
}

/// <summary>
/// A handler for one kind of persisted job. Handlers return an outcome for "nothing to do" and throw
/// <see cref="TransientExternalFailureException"/> or <see cref="PermanentExternalFailureException"/> for
/// failures, which is how the executor knows whether §14's retry applies.
/// </summary>
public interface IJobHandler
{
    JobType JobType { get; }

    Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one voice entry through the transcription endpoint (docs/开发指导.md §8.2).
/// <para>
/// The entry's own status is updated alongside the job's, in both directions. That is deliberate: the job table
/// answers "did the work run", and the entry answers "can the user see a transcript". Keeping them in step means
/// a crashed process never leaves a transcript that looks pending forever, or a failed job whose entry still
/// claims to be in progress.
/// </para>
/// </summary>
public sealed class TranscriptionJobHandler : IJobHandler
{
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly ITranscriptionClient _client;
    private readonly AssignTopicsAutomaticallyUseCase _assignTopics;
    private readonly Application.Embeddings.EnsureEmbeddingIndexedUseCase _ensureIndexed;
    private readonly ILogger<TranscriptionJobHandler> _logger;

    public TranscriptionJobHandler(
        IInputEntryRepository inputs,
        IAudioStore audio,
        ITranscriptionClient client,
        AssignTopicsAutomaticallyUseCase assignTopics,
        Application.Embeddings.EnsureEmbeddingIndexedUseCase ensureIndexed,
        ILogger<TranscriptionJobHandler> logger)
    {
        _inputs = inputs;
        _audio = audio;
        _client = client;
        _assignTopics = assignTopics;
        _ensureIndexed = ensureIndexed;
        _logger = logger;
    }

    public JobType JobType => JobType.Transcription;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Guid.TryParse(job.TargetId, out var parsed))
        {
            throw new PermanentExternalFailureException(
                "transcription.bad_target",
                "The job does not name a valid input entry.");
        }

        var entry = await _inputs
            .FindByIdAsync(new InputEntryId(parsed), cancellationToken)
            .ConfigureAwait(false);

        // Deleted or already transcribed: nothing to do. Succeeding keeps the queue clean, and §20's promise is
        // about not *losing* input, not about transcribing things the user removed.
        if (entry is null || entry.IsDeleted || entry.SourceType != InputSourceType.Voice ||
            entry.TranscriptionStatus == TranscriptionStatus.Succeeded)
        {
            return JobOutcome.Skipped;
        }

        if (!entry.HasAudio)
        {
            entry.FailTranscription("transcription.audio_missing");
            await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

            throw new PermanentExternalFailureException(
                "transcription.audio_missing",
                "The audio for this entry is no longer stored.");
        }

        // A recovered job can find the entry exactly as the interruption left it: still in progress. §14 requeues
        // the work rather than pretending it failed, so this attempt has to accept that state as "start again" —
        // which is what RetryTranscription already means. Without this the requeued job was refused by the domain
        // and failed terminally, leaving a transcript the user could never obtain.
        if (entry.TranscriptionStatus == TranscriptionStatus.InProgress)
        {
            entry.RetryTranscription();
        }

        entry.BeginTranscription();
        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await _client
                .TranscribeAsync(
                    new TranscriptionRequest(
                        ct => _audio.OpenReadAsync(entry.AudioPath!, ct),
                        BuildFileName(entry),
                        entry.AudioContentType ?? "application/octet-stream"),
                    cancellationToken)
                .ConfigureAwait(false);

            entry.CompleteTranscription(result.Text);
            await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

            // The transcript itself is never logged (§16); only the fact that it succeeded.
            _logger.LogInformation("Transcription completed for entry {EntryId}.", entry.Id);

            await RunPostTranscriptionStepsAsync(entry.Id, cancellationToken).ConfigureAwait(false);

            return JobOutcome.Completed;
        }
        catch (TransientExternalFailureException exception)
        {
            entry.FailTranscription(exception.Code);
            await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (PermanentExternalFailureException exception)
        {
            entry.FailTranscription(exception.Code);
            await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else is a defect here or in the client. The entry must still be left in a state the user can
            // retry: found by running the real pipeline, where an unclassified exception left the capture stuck in
            // progress and the retry endpoint then refused it, turning one bug into an unusable entry.
            _logger.LogError(
                exception,
                "Transcription of entry {EntryId} threw {ErrorType}.",
                entry.Id,
                exception.GetType().Name);

            entry.FailTranscription("transcription.unexpected");
            await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// §8.2 step 5: 成功后保存原始转写，识别主题并生成 Embedding.
    /// <para>
    /// Both steps are deliberately outside the transcription job's failure semantics. The transcript is already
    /// durable and correct at this point, so a topic-matching bug or an unavailable embedding endpoint must not
    /// mark the transcription failed — that would tell the user their recording could not be transcribed when it
    /// was. Each step records its own problem and the entry keeps its transcript either way.
    /// </para>
    /// </summary>
    private async Task RunPostTranscriptionStepsAsync(InputEntryId entryId, CancellationToken cancellationToken)
    {
        try
        {
            await _assignTopics.ExecuteAsync(entryId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Topic recognition is a convenience: the input stays usable and unfiled, and the user can file it.
            _logger.LogWarning(
                "Topic recognition for entry {EntryId} failed with {ErrorType}.",
                entryId,
                exception.GetType().Name);
        }

        var entry = await _inputs.FindByIdAsync(entryId, cancellationToken).ConfigureAwait(false);
        await _ensureIndexed
            .ExecuteAsync(entryId, entry?.TranscriptForGeneration, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A syntactically valid file name; the endpoint only uses the extension to sniff the container.</summary>
    private static string BuildFileName(InputEntry entry)
    {
        var extension = Path.GetExtension(entry.AudioPath ?? string.Empty);
        return string.IsNullOrEmpty(extension) ? $"{entry.Id}.bin" : $"{entry.Id}{extension}";
    }
}
