using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;

namespace DailyMusings.Application.Jobs;

public sealed record JobView(ProcessingJob Job);

/// <summary>Lists recent jobs for the admin page and the client's task list (§9.1, §13).</summary>
public sealed class ListJobsUseCase
{
    private readonly IJobRepository _jobs;

    public ListJobsUseCase(IJobRepository jobs) => _jobs = jobs;

    public async Task<IReadOnlyList<JobView>> ExecuteAsync(int limit, CancellationToken cancellationToken)
    {
        var jobs = await _jobs.ListRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        return jobs.Select(job => new JobView(job)).ToArray();
    }
}

/// <summary>
/// Puts a terminally failed job back in line (§13 POST /api/jobs/{id}/retry).
/// <para>
/// Only a failed job may be requeued. Re-running a succeeded job would duplicate its effects — the domain
/// refuses it, and that refusal is the reason this use case has no "force" option.
/// </para>
/// </summary>
public sealed class RetryJobUseCase
{
    private readonly IJobRepository _jobs;
    private readonly IClock _clock;

    public RetryJobUseCase(IJobRepository jobs, IClock clock)
    {
        _jobs = jobs;
        _clock = clock;
    }

    public async Task<JobView> ExecuteAsync(JobId jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("job.unknown", $"No job with id {jobId}.");

        job.Requeue(_clock.UtcNow);
        await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        return new JobView(job);
    }
}
