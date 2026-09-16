using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Embeddings;
using DailyMusings.Application.Jobs;
using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Time;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// Writes one day's reflection (docs/开发指导.md §8.4).
/// <para>
/// The handler is thin on purpose: eligibility was decided when the job was enqueued, so all this does is
/// translate outcomes and failures. The draft's own status is kept in step with the job's, exactly as the
/// transcription handler does, so a crashed process never leaves a day that looks like it is generating forever.
/// </para>
/// </summary>
public sealed class ReflectionGenerationJobHandler : IJobHandler
{
    private readonly GenerateReflectionUseCase _generate;
    private readonly IReflectionRepository _reflections;
    private readonly IClock _clock;
    private readonly ILogger<ReflectionGenerationJobHandler> _logger;

    public ReflectionGenerationJobHandler(
        GenerateReflectionUseCase generate,
        IReflectionRepository reflections,
        IClock clock,
        ILogger<ReflectionGenerationJobHandler> logger)
    {
        _generate = generate;
        _reflections = reflections;
        _clock = clock;
        _logger = logger;
    }

    public JobType JobType => JobType.ReflectionGeneration;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!ContentDate.TryParse(job.TargetId, out var contentDate))
        {
            throw new PermanentExternalFailureException(
                "reflection.bad_target",
                "The job does not name a valid content day.");
        }

        var payload = ReflectionGenerationPayload.FromJson(job.Payload);

        try
        {
            var result = await _generate.ExecuteAsync(contentDate, payload, cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ReflectionGenerationOutcome.Generated:
                    // The draft's text is never logged (§16); only the fact that it was produced and how many
                    // citations could not be located, which is a health signal rather than content.
                    _logger.LogInformation(
                        "Generated reflection for {ContentDate} with {UnresolvedCitationCount} unlocated citation(s).",
                        contentDate,
                        result.UnresolvedCitations);
                    return JobOutcome.Completed;

                case ReflectionGenerationOutcome.SkippedManualEditsProtected:
                    _logger.LogInformation(
                        "Reflection for {ContentDate} was not regenerated: the working version carries hand edits "
                        + "and nobody accepted losing them.",
                        contentDate);
                    return JobOutcome.Skipped;

                case ReflectionGenerationOutcome.SkippedAlreadyConfirmed:
                    return JobOutcome.Skipped;

                default:
                    return JobOutcome.Skipped;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkFailedAsync(contentDate, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Records the draft-side failure so the client can see it immediately instead of watching a day sit in
    /// "generating" between attempts. The status is allowed back out again from <c>Failed</c>, so a retry of the
    /// job re-enters generation normally and a permanently failed job leaves the day visibly failed rather than
    /// stuck.
    /// </summary>
    private async Task MarkFailedAsync(
        ContentDate contentDate,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception,
            "Reflection generation for {ContentDate} threw {ErrorType}.",
            contentDate,
            exception.GetType().Name);

        try
        {
            var reflection = await _reflections
                .FindByContentDateAsync(contentDate, cancellationToken)
                .ConfigureAwait(false);

            if (reflection is null || reflection.Status != Domain.Reflections.ReflectionStatus.Generating)
            {
                return;
            }

            reflection.MarkGenerationFailed(_clock.UtcNow);
            await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (cleanupFailure is not OperationCanceledException)
        {
            // The job's own failure is what matters; failing to annotate the draft must not replace it.
            _logger.LogWarning(
                "Could not mark the draft for {ContentDate} as failed: {ErrorType}.",
                contentDate,
                cleanupFailure.GetType().Name);
        }
    }
}

/// <summary>
/// Runs §8.4's second stage for one version.
/// <para>
/// A separate job from generation because the draft is already usable when this runs: if the check fails, the
/// user keeps their draft and the version simply reports that the check has not completed, which is exactly what
/// the <c>sources_checked_at_utc</c> stamp is for.
/// </para>
/// </summary>
public sealed class UnsourcedStatementCheckJobHandler : IJobHandler
{
    private readonly RunUnsourcedStatementCheckUseCase _check;
    private readonly ILogger<UnsourcedStatementCheckJobHandler> _logger;

    public UnsourcedStatementCheckJobHandler(
        RunUnsourcedStatementCheckUseCase check,
        ILogger<UnsourcedStatementCheckJobHandler> logger)
    {
        _check = check;
        _logger = logger;
    }

    public JobType JobType => JobType.UnsourcedStatementCheck;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Guid.TryParse(job.TargetId, out var parsed))
        {
            throw new PermanentExternalFailureException(
                "check.bad_target",
                "The job does not name a valid reflection version.");
        }

        var findings = await _check
            .ExecuteAsync(new ReflectionVersionId(parsed), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Source check completed with {FindingCount} finding(s).", findings);
        return JobOutcome.Completed;
    }
}

/// <summary>
/// Embeds one entry (§8.2 step 5).
/// <para>
/// Skipping is a normal outcome here: the endpoint may be switched off, the entry may have been deleted, or it
/// may have no usable text. Failing would put a pointless retry on the queue for something that cannot change.
/// </para>
/// </summary>
public sealed class EmbeddingIndexJobHandler : IJobHandler
{
    private readonly EmbedInputUseCase _embed;

    public EmbeddingIndexJobHandler(EmbedInputUseCase embed) => _embed = embed;

    public JobType JobType => JobType.EmbeddingIndex;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Guid.TryParse(job.TargetId, out var parsed))
        {
            throw new PermanentExternalFailureException(
                "embedding.bad_target",
                "The job does not name a valid input entry.");
        }

        var embedded = await _embed.ExecuteAsync(new InputEntryId(parsed), cancellationToken).ConfigureAwait(false);
        return embedded ? JobOutcome.Completed : JobOutcome.Skipped;
    }
}

/// <summary>
/// Rebuilds the semantic index for one configuration fingerprint (§8.3, decision A.8).
/// <para>
/// One batch per attempt, then <see cref="JobOutcome.Continue"/>: the job goes back to the queue without
/// spending a retry, so an archive of any size can be indexed inside §14's three-attempt budget. Retrieval keeps
/// using the previous index throughout, because the fingerprint only switches once nothing is left pending.
/// </para>
/// </summary>
public sealed class EmbeddingRebuildJobHandler : IJobHandler
{
    private readonly RebuildEmbeddingIndexUseCase _rebuild;

    public EmbeddingRebuildJobHandler(RebuildEmbeddingIndexUseCase rebuild) => _rebuild = rebuild;

    public JobType JobType => JobType.EmbeddingRebuild;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var result = await _rebuild.ExecuteAsync(job.TargetId, cancellationToken).ConfigureAwait(false);

        return result.Complete ? JobOutcome.Completed : JobOutcome.Continue;
    }
}
