using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// Reports that the background executor is alive (docs/开发指导.md §16 requires the basic health check to
/// cover the executor's state).
/// <para>
/// Kept in memory on purpose: this answers "is this process's executor loop running", not "did some past run
/// succeed". Durable job state lives in the database, where it survives a restart.
/// </para>
/// </summary>
public sealed class JobExecutorHeartbeat
{
    private long _lastBeatTicks;

    public void Beat(DateTimeOffset at) => Interlocked.Exchange(ref _lastBeatTicks, at.UtcTicks);

    public DateTimeOffset? LastBeat
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastBeatTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>True when a beat has landed recently enough to call the loop healthy.</summary>
    public bool IsFresh(DateTimeOffset now)
    {
        var last = LastBeat;
        return last is not null && now - last.Value <= JobExecutorService.Interval * 3;
    }
}

/// <summary>
/// The single background task executor (docs/开发指导.md §14).
/// <para>
/// It exists now, and beats, so that health checks and the compose topology are already correct: §14 requires
/// one executor instance, and §16 requires its state to be observable. The actual job handlers —
/// transcription, indexing, generation, notification, publication and the audio-retention sweep — are added
/// by the phases that define them; job rows and their idempotency keys are already modelled and migrated.
/// </para>
/// <para>
/// Note this is a <see cref="BackgroundService"/> singleton: it deliberately depends on nothing scoped, so it
/// cannot accidentally hold a database connection for the lifetime of the process.
/// </para>
/// </summary>
public sealed class JobExecutorService : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(5);

    private readonly JobExecutorHeartbeat _heartbeat;
    private readonly IClock _clock;
    private readonly ILogger<JobExecutorService> _logger;

    public JobExecutorService(
        JobExecutorHeartbeat heartbeat,
        IClock clock,
        ILogger<JobExecutorService> logger)
    {
        _heartbeat = heartbeat;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background job executor started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _heartbeat.Beat(_clock.UtcNow);

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
        finally
        {
            // A stopping executor must stop reporting healthy, otherwise a hung shutdown looks fine.
            _logger.LogInformation("Background job executor stopped.");
        }
    }
}
