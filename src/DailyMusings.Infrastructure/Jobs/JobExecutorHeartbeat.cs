namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// Reports that the background executor is alive (docs/开发指导.md §16 requires the basic health check to cover
/// the executor's state).
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

    /// <summary>
    /// True when a beat has landed recently enough to call the loop healthy. The loop beats once per iteration, so
    /// three intervals of silence means it is stuck rather than merely busy with a long job.
    /// </summary>
    public bool IsFresh(DateTimeOffset now)
    {
        var last = LastBeat;
        return last is not null && now - last.Value <= JobExecutorService.Interval * 3;
    }
}
