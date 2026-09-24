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
/// <see cref="Application.Abstractions.TransientExternalFailureException"/> or
/// <see cref="Application.Abstractions.PermanentExternalFailureException"/> for failures, which is how the
/// executor knows whether §14's retry applies.
/// </summary>
public interface IJobHandler
{
    JobType JobType { get; }

    Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one voice entry through the transcription endpoint (docs/开发指导.md §8.2).
/// <para>
/// A thin wrapper since the upload path started transcribing inline: the sequence itself lives in
/// <see cref="Application.Abstractions.ITranscriptionRunner"/>, and this handler's job is to be the durable
/// caller of it. That split is what keeps §14's "the work survives a restart" true — the inline attempt is a
/// convenience nobody depends on, while this is the path that is guaranteed to run and to retry.
/// </para>
/// </summary>
public sealed class TranscriptionJobHandler : IJobHandler
{
    private readonly Application.Abstractions.ITranscriptionRunner _runner;
    private readonly ILogger<TranscriptionJobHandler> _logger;

    public TranscriptionJobHandler(
        Application.Abstractions.ITranscriptionRunner runner,
        ILogger<TranscriptionJobHandler> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public JobType JobType => JobType.Transcription;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Guid.TryParse(job.TargetId, out var parsed))
        {
            throw new Application.Abstractions.PermanentExternalFailureException(
                "transcription.bad_target",
                "The job does not name a valid input entry.");
        }

        var entry = await _runner
            .RunAsync(new Domain.Common.InputEntryId(parsed), job.Payload, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            // Deleted, not a voice entry, or already transcribed. Nothing was wrong, so nothing is retried.
            _logger.LogDebug("Transcription job for {InputId} had nothing to do.", job.TargetId);
            return JobOutcome.Skipped;
        }

        return JobOutcome.Completed;
    }
}
