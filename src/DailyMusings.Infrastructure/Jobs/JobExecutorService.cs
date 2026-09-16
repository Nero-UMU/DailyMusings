using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// The single background task executor (docs/开发指导.md §14).
/// <para>
/// One instance only: §14 explicitly does not support multi-instance deployment, and the compose topology runs
/// exactly one. The exclusive database-side claim in <c>TryClaimAsync</c> still exists, so a mistake in that
/// topology degrades to "work is skipped" rather than "work runs twice".
/// </para>
/// <para>
/// It owns no scoped dependency. Each job gets its own scope, which is what keeps a long-lived background loop
/// from holding a database connection open for the lifetime of the process.
/// </para>
/// </summary>
public sealed class JobExecutorService : BackgroundService
{
    /// <summary>How long to wait when the queue is empty. Also the unit the health probe's freshness is scaled to.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(2);

    private const int BatchSize = 8;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobExecutorHeartbeat _heartbeat;
    private readonly IClock _clock;
    private readonly ILogger<JobExecutorService> _logger;

    public JobExecutorService(
        IServiceScopeFactory scopeFactory,
        JobExecutorHeartbeat heartbeat,
        IClock clock,
        ILogger<JobExecutorService> logger)
    {
        _scopeFactory = scopeFactory;
        _heartbeat = heartbeat;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background job executor started.");

        await RecoverInterruptedJobsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            _heartbeat.Beat(_clock.UtcNow);

            int processed;
            try
            {
                processed = await ProcessDueJobsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (processed == 0)
            {
                try
                {
                    await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        // A stopping executor must stop reporting healthy, otherwise a hung shutdown looks fine.
        _logger.LogInformation("Background job executor stopped.");
    }

    /// <summary>
    /// Returns jobs left running by a killed process to the queue. Safe unconditionally: this process has just
    /// started, so nothing can legitimately be running.
    /// </summary>
    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var recovered = await jobs.RecoverInterruptedAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);

        if (recovered > 0)
        {
            _logger.LogInformation("Recovered {RecoveredCount} interrupted job(s) after restart.", recovered);
        }
    }

    private async Task<int> ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessingJob> due;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            due = await jobs
                .ListDueAsync(_clock.UtcNow, BatchSize, cancellationToken)
                .ConfigureAwait(false);
        }

        var processed = 0;

        foreach (var candidate in due)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (await RunJobAsync(candidate.Id, cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>Claims and runs one job. Returns false when the claim was lost or the job vanished.</summary>
    private async Task<bool> RunJobAsync(JobId jobId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobs = services.GetRequiredService<IJobRepository>();

        // The claim is exclusive, so losing it means another executor already owns this job.
        if (!await jobs.TryClaimAsync(jobId, _clock.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Re-read rather than reuse the candidate: the claim incremented the attempt count and stamped the start.
        var job = await jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        var handler = services
            .GetServices<IJobHandler>()
            .FirstOrDefault(candidate => candidate.JobType == job.JobType);

        try
        {
            if (handler is null)
            {
                _logger.LogWarning("No handler is registered for job type {JobType}.", job.JobType);

                job.Fail(
                    "job.no_handler",
                    "No handler is registered for this job type.",
                    _clock.UtcNow,
                    new RetryPolicy(MaxAttempts: 1, BaseDelay: TimeSpan.FromSeconds(1)));
            }
            else
            {
                await handler.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
                job.Succeed(_clock.UtcNow);
            }
        }
        catch (TransientExternalFailureException exception)
        {
            // §14: temporary trouble backs off and is tried again, up to the bounded attempt count.
            job.Fail(exception.Code, exception.Message, _clock.UtcNow, RetryPolicy.Default);
        }
        catch (PermanentExternalFailureException exception)
        {
            // Retrying cannot help, so fail terminally on the first attempt rather than burning the retry budget.
            job.Fail(
                exception.Code,
                exception.Message,
                _clock.UtcNow,
                new RetryPolicy(MaxAttempts: 1, BaseDelay: TimeSpan.FromSeconds(1)));
        }
        catch (DomainException exception)
        {
            // A rule refused the work. Retrying the same state would refuse it again.
            job.Fail(
                exception.Code,
                "The job was refused by a domain rule.",
                _clock.UtcNow,
                new RetryPolicy(MaxAttempts: 1, BaseDelay: TimeSpan.FromSeconds(1)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-job. Deliberately leave it running: startup recovery will requeue it, which is
            // more honest than pretending it failed.
            throw;
        }
        catch (Exception exception)
        {
            // A defect. Only the type is recorded, never the message, which could carry private content (§16).
            _logger.LogError(
                exception,
                "Job {JobId} of type {JobType} threw {ErrorType}.",
                job.Id,
                job.JobType,
                exception.GetType().Name);

            job.Fail(
                "job.unexpected",
                exception.GetType().Name,
                _clock.UtcNow,
                RetryPolicy.Default);
        }

        await jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        if (job.Status == JobStatus.Failed)
        {
            // Notification of a terminal failure is itself a job (§12), enqueued by the phase that owns mail.
            _logger.LogWarning(
                "Job {JobId} of type {JobType} failed terminally with {ErrorCode}.",
                job.Id,
                job.JobType,
                job.ErrorCode);
        }

        return true;
    }
}
