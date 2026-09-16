using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Embeddings;
using DailyMusings.Application.Jobs;
using DailyMusings.Application.Publishing;
using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// The time-driven half of docs/开发指导.md §7: the nightly generation slot, and the catch-up scan after
/// downtime or an offline client.
/// <para>
/// It only ever <em>enqueues</em> work. Every rule about whether a day may be generated lives in
/// <see cref="GenerationRules"/> and is applied by <see cref="RequestReflectionGenerationUseCase"/>, so the
/// scheduled path, the manual path and the job path cannot drift apart — and a tick that decides nothing is
/// cheap, which is what makes a short interval affordable.
/// </para>
/// <para>
/// Deliberately quiet when generation is not configured: a fresh instance with no model endpoint must not fill
/// its queue with jobs that can only fail. Once an endpoint is configured, the same scan catches up on the days
/// that were skipped, because backfill is derived from the days that have material rather than from a marker
/// saying "we already ran".
/// </para>
/// </summary>
public sealed class ReflectionSchedulerService : BackgroundService
{
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGenerationSettingsProvider _generationSettings;
    private readonly IEmbeddingSettingsProvider _embeddingSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReflectionSchedulerService> _logger;

    public ReflectionSchedulerService(
        IServiceScopeFactory scopeFactory,
        IGenerationSettingsProvider generationSettings,
        IEmbeddingSettingsProvider embeddingSettings,
        IConfiguration configuration,
        ILogger<ReflectionSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _generationSettings = generationSettings;
        _embeddingSettings = embeddingSettings;
        _configuration = configuration;
        _logger = logger;
    }

    private TimeSpan Interval => TimeSpan.FromSeconds(
        Math.Clamp(_configuration.GetValue("Scheduler:IntervalSeconds", (int)DefaultInterval.TotalSeconds), 1, 3600));

    private int BackfillWindowDays => Math.Clamp(_configuration.GetValue("Scheduler:BackfillWindowDays", 60), 1, 3650);

    /// <summary>
    /// Bound on how many days one tick may put into generation. After a long outage every missed day is due at
    /// once, and queueing them all would fire a burst of model calls; the rest are picked up by later ticks.
    /// </summary>
    private int MaxGenerationsPerTick => Math.Clamp(_configuration.GetValue("Scheduler:MaxGenerationsPerTick", 3), 1, 100);

    private bool _warnedAboutMissingGeneration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reflection scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A scheduler that dies on one bad tick would silently stop producing drafts, which is worse than
                // a noisy log line. Nothing here is retried in place: the next tick re-derives the work from the
                // database, so a transient failure needs no special handling.
                _logger.LogError(
                    exception,
                    "Reflection scheduler tick failed with {ErrorType}.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Reflection scheduler stopped.");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var generation = await _generationSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!generation.Enabled)
        {
            if (!_warnedAboutMissingGeneration)
            {
                _warnedAboutMissingGeneration = true;
                _logger.LogInformation(
                    "No generation endpoint is configured, so no drafts will be produced. "
                    + "Configure Generation:Enabled and the endpoint to start.");
            }

            return;
        }

        await ScheduleGenerationsAsync(cancellationToken).ConfigureAwait(false);
        await ScheduleEmbeddingRebuildAsync(cancellationToken).ConfigureAwait(false);
        await SchedulePublicationsAsync(cancellationToken).ConfigureAwait(false);
        await ScheduleMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The two daily housekeeping jobs (§15.1, §15.2): a complete backup and the recording retention sweep.
    /// <para>
    /// Both are keyed on the content day, so a scan that runs every couple of seconds still produces exactly one of
    /// each per day — and a restart in the middle of the night does not produce a second backup.
    /// </para>
    /// </summary>
    private async Task ScheduleMaintenanceAsync(CancellationToken cancellationToken)
    {
        var backupEnabled = _configuration.GetValue("Maintenance:BackupEnabled", true);
        var backupTime = ReadLocalTime("Maintenance:BackupLocalTime", new TimeOnly(3, 30));
        var cleanupTime = ReadLocalTime("Maintenance:AudioCleanupLocalTime", new TimeOnly(4, 0));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var calendars = services.GetRequiredService<IContentCalendarProvider>();
        var clock = services.GetRequiredService<IClock>();
        var jobs = services.GetRequiredService<JobEnqueuer>();

        var calendar = await calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        // Yesterday's content day is the one whose slot has certainly arrived, so nothing is scheduled for a day
        // that is still in progress.
        var day = calendar.ContentDateOf(now);

        if (backupEnabled && now >= calendar.AtLocalTime(day, backupTime))
        {
            await jobs.EnsureAsync(
                JobType.Backup,
                day.ToString(),
                $"backup:{day}",
                payload: null,
                requeueFailed: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (now >= calendar.AtLocalTime(day, cleanupTime))
        {
            await jobs.EnsureAsync(
                JobType.AudioCleanup,
                day.ToString(),
                $"audio-cleanup:{day}",
                payload: null,
                requeueFailed: false,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeOnly ReadLocalTime(string key, TimeOnly fallback) =>
        TimeOnly.TryParseExact(
            _configuration.GetValue<string?>(key),
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// The publish half of the clock (§11.1): the 08:00 slot, the execution window, and the invalidation of a
    /// pending version when the draft moves on. It enqueues and records; the rules it applies live in the domain.
    /// </summary>
    private async Task SchedulePublicationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var schedule = scope.ServiceProvider.GetRequiredService<SchedulePublicationsUseCase>();

        var result = await schedule.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (result.Queued > 0 || result.Expired > 0 || result.Superseded > 0)
        {
            // Counts only — never a title, a target's credentials or an article id (§16).
            _logger.LogInformation(
                "Publish schedule: {QueuedCount} queued, {ExpiredCount} expired, {SupersededCount} superseded.",
                result.Queued,
                result.Expired,
                result.Superseded);
        }
    }

    private async Task ScheduleGenerationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var calendars = services.GetRequiredService<IContentCalendarProvider>();
        var clock = services.GetRequiredService<IClock>();
        var inputs = services.GetRequiredService<IInputEntryRepository>();
        var reflections = services.GetRequiredService<IReflectionRepository>();
        var settings = services.GetRequiredService<IContentSettingsProvider>();
        var request = services.GetRequiredService<RequestReflectionGenerationUseCase>();

        var calendar = await calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var content = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var today = calendar.ContentDateOf(now);

        // Days with material, from the input side: a day with nothing in it is never a candidate, which is how
        // §7's "no input means no article" holds without a separate guard.
        var daysWithInputs = await inputs
            .ListContentDatesWithInputsAsync(today, BackfillWindowDays, cancellationToken)
            .ConfigureAwait(false);

        if (daysWithInputs.Count == 0)
        {
            return;
        }

        var existing = await reflections
            .ListByDateRangeAsync(today.AddDays(-(BackfillWindowDays - 1)), today, cancellationToken)
            .ConfigureAwait(false);

        var byDate = existing.ToDictionary(reflection => reflection.ContentDate);

        var due = daysWithInputs
            .Where(day => GenerationRules.IsSlotDue(day, now, calendar, content.GenerationLocalTime))
            .Where(day =>
            {
                var status = byDate.TryGetValue(day, out var reflection)
                    ? reflection.Status
                    : (ReflectionStatus?)null;

                // Pre-filter only. The use case re-applies the rules with the real material set, including
                // whether a transcription failure is still blocking the day.
                return GenerationRules.ForScheduledRun(status, hasMaterial: true, hasBlockingFailures: false).Allowed;
            })

            // Oldest first: §7 asks for day-by-day catch-up, and building the history in order means a backfilled
            // day can cite the days before it.
            .OrderBy(day => day)
            .Take(MaxGenerationsPerTick)
            .ToArray();

        var enqueued = 0;

        foreach (var day in due)
        {
            var result = await request
                .ExecuteAsync(
                    day,
                    manual: false,
                    ignoreTranscriptionFailures: false,
                    allowOverwriteOfManualEdits: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Job is not null)
            {
                enqueued++;
            }
        }

        if (enqueued > 0)
        {
            // Only the day and the count — never the material (§16).
            _logger.LogInformation(
                "Scheduler queued generation for {QueuedCount} day(s): {Days}.",
                enqueued,
                string.Join(", ", due.Select(day => day.ToString())));
        }
    }

    /// <summary>
    /// Enqueues a rebuild when the configured embedding fingerprint is not the one the index was built for
    /// (decision A.8). Retrieval keeps working from topics and full text until the rebuild completes.
    /// </summary>
    private async Task ScheduleEmbeddingRebuildAsync(CancellationToken cancellationToken)
    {
        var embedding = await _embeddingSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!embedding.Enabled)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var state = services.GetRequiredService<IEmbeddingIndexState>();
        var jobs = services.GetRequiredService<JobEnqueuer>();

        var configured = embedding.ResolveConfigVersion().Fingerprint;
        var indexed = await state.GetIndexedVersionAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(indexed, configured, StringComparison.Ordinal))
        {
            return;
        }

        await jobs.EnsureAsync(
            JobType.EmbeddingRebuild,
            configured,
            IdempotencyKeys.IndexRebuild(configured),
            payload: null,
            requeueFailed: false,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Semantic search is rebuilding: the index does not match the configured model.");
    }
}
