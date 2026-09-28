using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;

namespace DailyMusings.Application.Operations;

/// <summary>
/// Turns the temporary debug mode on and off (docs/开发指导.md §16).
/// <para>
/// Enabling it is an administrator action with a mandatory duration: the guide says the mode must expire on its own,
/// and a mode that only ends when somebody remembers to end it is not a temporary mode. The maximum is bounded here
/// rather than trusted to the caller, so a client cannot ask for a year.
/// </para>
/// </summary>
public sealed class ManageDiagnosticModeUseCase
{
    /// <summary>The most a single request may turn it on for.</summary>
    public static TimeSpan MaximumDuration { get; } = TimeSpan.FromHours(4);

    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(30);

    private readonly IDiagnosticMode _mode;
    private readonly IClock _clock;

    public ManageDiagnosticModeUseCase(IDiagnosticMode mode, IClock clock)
    {
        _mode = mode;
        _clock = clock;
    }

    public Task<DiagnosticModeState> GetAsync(CancellationToken cancellationToken) =>
        _mode.GetAsync(cancellationToken);

    public async Task<DiagnosticModeState> EnableAsync(
        string enabledBy,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledBy);

        var requested = duration ?? DefaultDuration;

        if (requested <= TimeSpan.Zero || requested > MaximumDuration)
        {
            throw new UseCaseException(
                "diagnostics.duration.out_of_range",
                $"The debug mode may be enabled for between one second and {MaximumDuration.TotalHours:0} hours.");
        }

        _ = _clock;

        return await _mode.EnableAsync(enabledBy, requested, cancellationToken).ConfigureAwait(false);
    }

    public Task DisableAsync(CancellationToken cancellationToken) => _mode.DisableAsync(cancellationToken);
}

/// <summary>Asks an external service whether it is reachable (docs/开发指导.md §16, §13).</summary>
public sealed class ProbeExternalServiceUseCase
{
    private readonly IExternalServiceProbe _probe;

    public ProbeExternalServiceUseCase(IExternalServiceProbe probe) => _probe = probe;

    public Task<ProbeResult> ExecuteAsync(ExternalService service, CancellationToken cancellationToken) =>
        _probe.ProbeAsync(service, cancellationToken);

    public Task<ProbeResult> ExecuteModelAsync(
        ModelEndpointProbeRequest request,
        CancellationToken cancellationToken) =>
        _probe.ProbeModelAsync(request, cancellationToken);
}

/// <summary>
/// Starts an index rebuild on demand (docs/开发指导.md §8.3).
/// <para>
/// The scheduler already rebuilds when the configured embedding model changes; this is the same job asked for by a
/// person — for instance after adding an embedding endpoint to an instance that had none, where no fingerprint change
/// ever happened because there was no fingerprint before.
/// </para>
/// </summary>
public sealed class RequestIndexRebuildUseCase
{
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly IEmbeddingIndexRepository _index;
    private readonly IEmbeddingIndexState _state;
    private readonly Application.Jobs.JobEnqueuer _jobs;

    public RequestIndexRebuildUseCase(
        IEmbeddingSettingsProvider settings,
        IEmbeddingIndexRepository index,
        IEmbeddingIndexState state,
        Application.Jobs.JobEnqueuer jobs)
    {
        _settings = settings;
        _index = index;
        _state = state;
        _jobs = jobs;
    }

    /// <returns>The fingerprint being rebuilt, or <c>null</c> when embeddings are switched off.</returns>
    public async Task<string?> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            // Nothing to build. Reported as nothing rather than as a failure: §3.1 keeps the endpoint optional, and
            // an instance without one is not broken.
            return null;
        }

        var configured = settings.ResolveConfigVersion().Fingerprint;

        await _jobs.EnsureAsync(
            JobType.EmbeddingRebuild,
            configured,
            IdempotencyKeys.IndexRebuild(configured),
            payload: null,

            // A terminal failure means a human has to look at it; asking again is that human, so it is requeued.
            requeueFailed: true,
            cancellationToken).ConfigureAwait(false);

        return configured;
    }
}

/// <summary>How many entries the index holds, and which fingerprint it was built for.</summary>
public sealed record IndexStatus(string? IndexedVersion, string? ConfiguredVersion, int IndexedEntries, bool SemanticSearchAvailable)
{
    public bool RebuildInProgress => ConfiguredVersion is not null && !SemanticSearchAvailable;
}

/// <summary>Reports the index state for the admin page (§8.3, A.8).</summary>
public sealed class GetIndexStatusUseCase
{
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly IEmbeddingIndexRepository _index;
    private readonly IEmbeddingIndexState _state;

    public GetIndexStatusUseCase(
        IEmbeddingSettingsProvider settings,
        IEmbeddingIndexRepository index,
        IEmbeddingIndexState state)
    {
        _settings = settings;
        _index = index;
        _state = state;
    }

    public async Task<IndexStatus> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            return new IndexStatus(null, null, 0, false);
        }

        var configured = settings.ResolveConfigVersion().Fingerprint;
        var indexed = await _state.GetIndexedVersionAsync(cancellationToken).ConfigureAwait(false);
        var count = await _index.CountAsync(configured, cancellationToken).ConfigureAwait(false);

        return new IndexStatus(
            indexed,
            configured,
            count,
            SemanticSearchAvailable: string.Equals(indexed, configured, StringComparison.Ordinal));
    }
}

/// <summary>The state of one maintenance operation, as the admin page reports it.</summary>
public sealed record MaintenanceSummary(string Name, int Pending, int Failed, string? LastRunUtc)
{
    public static MaintenanceSummary From(string name, IEnumerable<ProcessingJob> jobs)
    {
        var relevant = jobs.Where(job => job.JobType.ToString() == name).ToArray();

        return new MaintenanceSummary(
            name,
            relevant.Count(job => job.Status == JobStatus.Pending),
            relevant.Count(job => job.Status == JobStatus.Failed),
            relevant
                .Where(job => job.CompletedAtUtc is not null)
                .OrderByDescending(job => job.CompletedAtUtc)
                .FirstOrDefault()
                ?.CompletedAtUtc
                ?.ToString("o", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Puts a maintenance job on the queue now, for the admin page's "run it again" buttons
/// (docs/开发指导.md §14: 用户修复配置后可以重试单项或批量补跑失败任务).
/// </summary>
public sealed class RunMaintenanceJobUseCase
{
    private readonly Application.Jobs.JobEnqueuer _jobs;
    private readonly IClock _clock;

    public RunMaintenanceJobUseCase(Application.Jobs.JobEnqueuer jobs, IClock clock)
    {
        _jobs = jobs;
        _clock = clock;
    }

    /// <param name="jobType">
    /// Backup, AudioCleanup or ContentCleanup; anything else is refused, because the queue is not a shell.
    /// </param>
    public async Task<ProcessingJob> ExecuteAsync(JobType jobType, CancellationToken cancellationToken)
    {
        if (jobType is not (JobType.Backup or JobType.AudioCleanup or JobType.ContentCleanup))
        {
            throw new UseCaseException(
                "maintenance.unsupported_job",
                $"{jobType} is not a maintenance job that can be started by hand.");
        }

        // A fresh key per request: an operator pressing the button twice in a minute wants two runs, not one, and
        // the daily key would otherwise answer the second press with the first run's job.
        var key = $"{jobType.ToString().ToLowerInvariant()}:manual:{_clock.UtcNow:yyyyMMddHHmmss}";

        return await _jobs.EnsureAsync(
            jobType,
            _clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            key,
            payload: null,
            requeueFailed: true,
            cancellationToken).ConfigureAwait(false);
    }
}
