using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;

namespace DailyMusings.Application.Jobs;

/// <summary>
/// Puts a unit of work on the durable queue exactly once per idempotency key
/// (docs/开发指导.md §14: 任务必须持久化，同一输入…同一日期的生成…都必须幂等).
/// <para>
/// Why this is a separate collaborator rather than a line in each use case: the "have I already asked for
/// this?" question has to be answered identically by the nightly scheduler, which runs every minute, and by an
/// HTTP request, which a user may send repeatedly. Answering it in one place is what keeps both from queueing
/// duplicate model calls.
/// </para>
/// </summary>
public sealed class JobEnqueuer
{
    private readonly IJobRepository _jobs;
    private readonly IClock _clock;

    public JobEnqueuer(IJobRepository jobs, IClock clock)
    {
        _jobs = jobs;
        _clock = clock;
    }

    /// <summary>
    /// Returns the job for <paramref name="idempotencyKey"/>, creating it when it does not exist yet.
    /// </summary>
    /// <param name="requeueFailed">
    /// Whether a terminally failed job may be put back in line. Only an explicit user request sets this: the
    /// scheduler must not resurrect failures, or §14's bounded retry would become an unbounded one.
    /// </param>
    public async Task<ProcessingJob> EnsureAsync(
        JobType jobType,
        string targetId,
        string idempotencyKey,
        string? payload,
        bool requeueFailed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var existing = await _jobs.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await RequeueIfAskedAsync(existing, requeueFailed, cancellationToken).ConfigureAwait(false);
        }

        var job = ProcessingJob.Create(JobId.New(), jobType, targetId, idempotencyKey, _clock.UtcNow, payload);

        try
        {
            await _jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            return job;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Two requests can pass the lookup above at the same time; the table's unique index then refuses
            // the second insert. Losing that race is not an error — the work is queued either way — so the
            // winner is read back. Any other failure has no row to return and is rethrown below.
            var winner = await _jobs.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return await RequeueIfAskedAsync(winner, requeueFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessingJob> RequeueIfAskedAsync(
        ProcessingJob job,
        bool requeueFailed,
        CancellationToken cancellationToken)
    {
        if (!requeueFailed || job.Status != JobStatus.Failed)
        {
            return job;
        }

        job.Requeue(_clock.UtcNow);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        return job;
    }
}
