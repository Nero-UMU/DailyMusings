using DailyMusings.Application.Abstractions;

namespace DailyMusings.Application.System;

public sealed record HealthReport(
    bool Healthy,
    IReadOnlyList<HealthProbeResult> Probes,
    DateTimeOffset CheckedAtUtc);

/// <summary>
/// Aggregates the basic health probes (docs/开发指导.md §16).
/// <para>
/// Only instance-local probes participate. Model and SMTP reachability are answered by their own
/// "test connection" actions: §16 is explicit that an unhealthy third party must not make this instance
/// report itself as broken.
/// </para>
/// </summary>
public sealed class SystemHealthUseCase
{
    private readonly IReadOnlyList<IHealthProbe> _probes;
    private readonly IClock _clock;
    private readonly IMigrationRunner _migrations;

    public SystemHealthUseCase(
        IEnumerable<IHealthProbe> probes,
        IMigrationRunner migrations,
        IClock clock)
    {
        _probes = probes.ToArray();
        _migrations = migrations;
        _clock = clock;
    }

    public async Task<HealthReport> ExecuteAsync(CancellationToken cancellationToken)
    {
        var results = new List<HealthProbeResult>(_probes.Count + 1);

        // Reporting the applied migration set makes "which schema is this instance on" answerable from the
        // health endpoint, which is the first thing anyone asks after a failed upgrade.
        var applied = await _migrations.GetAppliedAsync(cancellationToken).ConfigureAwait(false);
        results.Add(HealthProbeResult.Ok("database.migrations", applied.Count == 0 ? "none" : applied[^1]));

        foreach (var probe in _probes)
        {
            results.Add(await probe.CheckAsync(cancellationToken).ConfigureAwait(false));
        }

        return new HealthReport(
            results.All(result => result.Healthy),
            results,
            _clock.UtcNow);
    }
}

/// <summary>Reads instance status for the admin page: counts and last-error summaries, never content.</summary>
public sealed record InstanceStatus(
    DateTimeOffset CheckedAtUtc,
    bool AdministratorInitialized,
    int PairedDeviceCount,
    int RevokedDeviceCount);
