using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Embeddings;
using DailyMusings.Application.Topics;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Transcription;

/// <summary>
/// The one implementation of "transcribe this entry" (docs/开发指导.md §8.2 step 5).
/// <para>
/// Called from two places: the durable job handler, which is how the work is guaranteed to happen, and the
/// upload path, which calls it inline so the phone gets its text with the response instead of polling. Keeping
/// one implementation is what stops the two paths from disagreeing about what "transcribed" means — and the
/// inline caller is best-effort by construction, so a failure here costs a transcript a moment later, never the
/// capture.
/// </para>
/// <para>
/// The entry's own status is updated alongside the work, in both directions, because the job table answers "did
/// the work run" while the entry answers "can the user see a transcript". A crash between the two would
/// otherwise leave a transcript that looks pending forever, or a failed job whose entry still claims to be in
/// progress.
/// </para>
/// </summary>
public sealed class TranscriptionRunner : ITranscriptionRunner
{
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly ITranscriptionClient _client;
    private readonly AssignTopicsAutomaticallyUseCase _assignTopics;
    private readonly EnsureEmbeddingIndexedUseCase _ensureIndexed;
    private readonly ILogger<TranscriptionRunner> _logger;

    public TranscriptionRunner(
        IInputEntryRepository inputs,
        IAudioStore audio,
        ITranscriptionClient client,
        AssignTopicsAutomaticallyUseCase assignTopics,
        EnsureEmbeddingIndexedUseCase ensureIndexed,
        ILogger<TranscriptionRunner> logger)
    {
        _inputs = inputs;
        _audio = audio;
        _client = client;
        _assignTopics = assignTopics;
        _ensureIndexed = ensureIndexed;
        _logger = logger;
    }

    /// <returns>
    /// The refreshed entry, or <c>null</c> when there was nothing to do: unknown, deleted, not a voice entry,
    /// or already transcribed. Succeeding on "nothing to do" is deliberate — failing would only produce a
    /// pointless retry storm, and §20's promise is about not <em>losing</em> input, not about transcribing
    /// things the user removed.
    /// </returns>
    public async Task<InputEntry?> RunAsync(
        InputEntryId inputId,
        string? jobPayloadJson,
        CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(inputId, cancellationToken).ConfigureAwait(false);

        if (entry is null || entry.IsDeleted || entry.SourceType != InputSourceType.Voice ||
            entry.TranscriptionStatus == TranscriptionStatus.Succeeded)
        {
            return null;
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

            // Re-read before writing. The upload path calls this directly while the queued job may be running
            // the very same work in the executor, so "another writer got there first" is a normal outcome, not a
            // defect: the first transcript is the one that counts and the second attempt simply reports it.
            var current = await _inputs.FindByIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);

            if (current is null)
            {
                return null;
            }

            if (current.TranscriptionStatus == TranscriptionStatus.Succeeded)
            {
                return current;
            }

            current.CompleteTranscription(result.Text);
            await _inputs.UpdateAsync(current, cancellationToken).ConfigureAwait(false);

            // The transcript itself is never logged (§16); only the fact that it succeeded.
            _logger.LogInformation("Transcription completed for entry {EntryId}.", current.Id);

            await RunPostTranscriptionStepsAsync(current.Id, cancellationToken).ConfigureAwait(false);

            return current;
        }
        catch (TransientExternalFailureException exception)
        {
            await FailAsync(entry, exception.Code, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (PermanentExternalFailureException exception)
        {
            await FailAsync(entry, exception.Code, cancellationToken).ConfigureAwait(false);
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

            await FailAsync(entry, "transcription.unexpected", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Records the failure on the entry.
    /// <para>
    /// Cancellation is not a failure of the transcription: on the inline path it means the bounded wait expired
    /// and the queued job is about to do the work properly, so flipping the entry to <c>Failed</c> would put a
    /// red status on a capture that is fine. The caller decides; this only runs for real failures.
    /// </para>
    /// <para>
    /// An entry somebody else has already transcribed is left alone: the two callers of this class can overlap,
    /// and "the work succeeded while I was failing" must not be turned into a failure by the loser.
    /// </para>
    /// </summary>
    private async Task FailAsync(InputEntry entry, string code, CancellationToken cancellationToken)
    {
        var current = await _inputs.FindByIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);

        if (current is null || current.TranscriptionStatus == TranscriptionStatus.Succeeded)
        {
            return;
        }

        current.FailTranscription(code);
        await _inputs.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// §8.2 step 5: 成功后保存原始转写，识别主题并生成 Embedding.
    /// <para>
    /// Both steps are deliberately outside the transcription's failure semantics. The transcript is already
    /// durable and correct at this point, so a topic-matching bug or an unavailable embedding endpoint must not
    /// mark the transcription failed — that would tell the user their recording could not be transcribed when
    /// it was. Each step records its own problem and the entry keeps its transcript either way.
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
