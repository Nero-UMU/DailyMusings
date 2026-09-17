using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Configuration;

/// <summary>
/// Reads the instance's operational settings (docs/开发指导.md §8.1).
/// <para>
/// Settings table first, deployment configuration second, built-in default last — the same order the model and
/// SMTP editors use, so "what the admin page saved wins" is one rule across the product rather than a per-module
/// surprise. Read per call, never cached at start-up: an operator who changes the scheduler interval expects the
/// next tick to honour it, not the next restart.
/// </para>
/// </summary>
public sealed class AppSettingInstanceSettingsProvider : IInstanceSettingsProvider
{
    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public AppSettingInstanceSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<InstanceSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var defaults = InstanceSettings.Default;
        var scheduler = _configuration.GetSection("Scheduler");
        var maintenance = _configuration.GetSection("Maintenance");
        var backup = _configuration.GetSection("Backup");
        var retrieval = _configuration.GetSection("Retrieval");

        return new InstanceSettings(
            schedulerIntervalSeconds: StoredSettings.Integer(
                stored,
                InstanceSettings.SchedulerIntervalKey,
                scheduler.GetValue("IntervalSeconds", defaults.SchedulerIntervalSeconds)),
            schedulerBackfillWindowDays: StoredSettings.Integer(
                stored,
                InstanceSettings.SchedulerBackfillKey,
                scheduler.GetValue("BackfillWindowDays", defaults.SchedulerBackfillWindowDays)),
            schedulerMaxGenerationsPerTick: StoredSettings.Integer(
                stored,
                InstanceSettings.SchedulerMaxGenerationsKey,
                scheduler.GetValue("MaxGenerationsPerTick", defaults.SchedulerMaxGenerationsPerTick)),
            backupEnabled: StoredSettings.Boolean(
                stored,
                InstanceSettings.BackupEnabledKey,
                maintenance.GetValue("BackupEnabled", defaults.BackupEnabled)),
            backupLocalTime: StoredSettings.TimeOfDay(
                stored,
                InstanceSettings.BackupLocalTimeKey,
                ReadTime(maintenance, "BackupLocalTime", defaults.BackupLocalTime)),
            audioCleanupLocalTime: StoredSettings.TimeOfDay(
                stored,
                InstanceSettings.AudioCleanupLocalTimeKey,
                ReadTime(maintenance, "AudioCleanupLocalTime", defaults.AudioCleanupLocalTime)),
            backupKeepCount: StoredSettings.Integer(
                stored,
                InstanceSettings.BackupKeepCountKey,
                backup.GetValue("KeepCount", defaults.BackupKeepCount)),
            retrievalMaxMaterials: StoredSettings.Integer(
                stored,
                InstanceSettings.RetrievalMaxMaterialsKey,
                retrieval.GetValue("MaxMaterials", defaults.RetrievalMaxMaterials)),
            retrievalCandidateScanLimit: StoredSettings.Integer(
                stored,
                InstanceSettings.RetrievalCandidateScanLimitKey,
                retrieval.GetValue("CandidateScanLimit", defaults.RetrievalCandidateScanLimit)),
            retrievalMinimumRelevance: StoredSettings.Decimal(
                stored,
                InstanceSettings.RetrievalMinimumRelevanceKey,
                retrieval.GetValue("MinimumRelevance", defaults.RetrievalMinimumRelevance)),
            retrievalMinimumLexicalScore: StoredSettings.Decimal(
                stored,
                InstanceSettings.RetrievalMinimumLexicalScoreKey,
                retrieval.GetValue("MinimumLexicalScore", defaults.RetrievalMinimumLexicalScore)));
    }

    /// <summary>
    /// The two housekeeping slots, which were "<c>HH:mm</c>" strings in configuration. A malformed value falls
    /// back to the default rather than stopping the scheduler: a typo in a compose file must not silently disable
    /// the nightly backup, and the admin page is the place that reports a bad format.
    /// </summary>
    private static TimeOnly ReadTime(IConfigurationSection section, string key, TimeOnly fallback) =>
        TimeOnly.TryParseExact(
            section.GetValue<string?>(key),
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : fallback;
}
