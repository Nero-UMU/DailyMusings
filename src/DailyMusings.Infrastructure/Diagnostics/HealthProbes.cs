using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Jobs;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Storage;

namespace DailyMusings.Infrastructure.Diagnostics;

/// <summary>
/// Proves the database file can actually be written, not merely opened.
/// <para>
/// It writes to <c>PRAGMA user_version</c> and stores the value straight back. That is a real write to the
/// database header, it needs no schema of its own, and it leaves nothing behind — which matters because a
/// health probe that creates tables would be a schema change hiding inside monitoring.
/// </para>
/// </summary>
public sealed class DatabaseWritableProbe : IHealthProbe
{
    private readonly SqliteConnectionAccessor _accessor;

    public DatabaseWritableProbe(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public string Name => "database.writable";

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await _accessor
                .QuerySingleAsync("PRAGMA user_version;", reader => reader.GetInt64(0), cancellationToken)
                .ConfigureAwait(false);

            await _accessor
                .ExecuteAsync(
                    string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version};"),
                    cancellationToken)
                .ConfigureAwait(false);

            return HealthProbeResult.Ok(Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type is reported: §16 keeps private content out of logs and diagnostics.
            return HealthProbeResult.Fail(Name, exception.GetType().Name);
        }
    }
}

/// <summary>
/// Proves audio can be written where the product expects to write it. A read-only or unmounted media volume
/// is the classic container misconfiguration, and it must be caught before the first recording fails.
/// </summary>
public sealed class MediaDirectoryWritableProbe : IHealthProbe
{
    private readonly InstancePaths _paths;

    public MediaDirectoryWritableProbe(InstancePaths paths) => _paths = paths;

    public string Name => "media.writable";

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var probeFile = Path.Combine(_paths.MediaPath, $".health-{Guid.CreateVersion7():N}.tmp");

        try
        {
            Directory.CreateDirectory(_paths.MediaPath);
            await File.WriteAllTextAsync(probeFile, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probeFile);

            return HealthProbeResult.Ok(Name, _paths.MediaPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(probeFile);
            return HealthProbeResult.Fail(Name, exception.GetType().Name);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nothing useful to do; the failure that got us here is the one worth reporting.
        }
    }
}

/// <summary>
/// Proves the background executor is running. Without this, an instance whose executor died would still look
/// perfectly healthy while silently never generating anything.
/// </summary>
public sealed class JobExecutorProbe : IHealthProbe
{
    private readonly JobExecutorHeartbeat _heartbeat;
    private readonly IClock _clock;

    public JobExecutorProbe(JobExecutorHeartbeat heartbeat, IClock clock)
    {
        _heartbeat = heartbeat;
        _clock = clock;
    }

    public string Name => "jobExecutor.alive";

    public Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (_heartbeat.LastBeat is not { } lastBeat)
        {
            return Task.FromResult(HealthProbeResult.Fail(Name, "no heartbeat recorded yet"));
        }

        return Task.FromResult(
            _heartbeat.IsFresh(now)
                ? HealthProbeResult.Ok(Name)
                : HealthProbeResult.Fail(
                    Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"last heartbeat {now - lastBeat:g} ago")));
    }
}
