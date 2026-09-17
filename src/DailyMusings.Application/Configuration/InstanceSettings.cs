using System.Globalization;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Application.Configuration;

/// <summary>
/// The instance's operational tuning (docs/开发指导.md §7, §8.3, §15.1, §15.2).
/// <para>
/// These used to live only in deployment configuration, which meant the answer to "how often does the scheduler
/// run" or "how many backups are kept" was "edit compose and restart". They are ordinary settings-table values
/// now, read per call like everything else the admin page owns, so a change applies without a restart. The
/// deployment configuration and the built-in defaults below remain as fallbacks, so an instance configured
/// entirely from compose keeps working exactly as it did.
/// </para>
/// <para>
/// Keys are stable strings: they are persisted in the settings table and travel in backups and readable exports.
/// </para>
/// </summary>
public sealed record InstanceSettings
{
    // §7: how often the scheduler looks for work.
    public const string SchedulerIntervalKey = "scheduler.intervalSeconds";
    public const string SchedulerBackfillKey = "scheduler.backfillWindowDays";
    public const string SchedulerMaxGenerationsKey = "scheduler.maxGenerationsPerTick";

    // §15.1, §15.2: the nightly housekeeping slots and how many archives survive.
    public const string BackupEnabledKey = "maintenance.backupEnabled";
    public const string BackupLocalTimeKey = "maintenance.backupLocalTime";
    public const string AudioCleanupLocalTimeKey = "maintenance.audioCleanupLocalTime";
    public const string BackupKeepCountKey = "backup.keepCount";

    // §8.3: retrieval tuning when the embedding endpoint is off or unavailable.
    public const string RetrievalMaxMaterialsKey = "retrieval.maxMaterials";
    public const string RetrievalCandidateScanLimitKey = "retrieval.candidateScanLimit";
    public const string RetrievalMinimumRelevanceKey = "retrieval.minimumRelevance";
    public const string RetrievalMinimumLexicalScoreKey = "retrieval.minimumLexicalScore";

    /// <summary>Matches <c>Scheduler:IntervalSeconds</c>'s documented default (30s, not the executor's 2s).</summary>
    public const int DefaultSchedulerIntervalSeconds = 30;

    public const int DefaultSchedulerBackfillDays = 60;
    public const int DefaultSchedulerMaxGenerationsPerTick = 3;

    public const bool DefaultBackupEnabled = true;

    /// <summary>03:30 local, per §15.2.</summary>
    public static readonly TimeOnly DefaultBackupLocalTime = new(3, 30);

    /// <summary>04:00 local, after the backup.</summary>
    public static readonly TimeOnly DefaultAudioCleanupLocalTime = new(4, 0);

    /// <summary>Seven archives, per §15.2.</summary>
    public const int DefaultBackupKeepCount = 7;

    // Bounds. Wide enough not to get in the way, narrow enough that a typo cannot wedge the instance: an interval
    // of zero would spin the scheduler, and a candidate limit of a million would scan the whole history per query.
    public const int MinimumSchedulerIntervalSeconds = 1;
    public const int MaximumSchedulerIntervalSeconds = 3600;
    public const int MinimumBackfillDays = 1;
    public const int MaximumBackfillDays = 3650;
    public const int MaximumGenerationsPerTick = 100;
    public const int MinimumBackupKeepCount = 1;
    public const int MaximumBackupKeepCount = 365;
    public const int MaximumRetrievalMaterials = 100;
    public const int MaximumCandidateScanLimit = 20_000;

    public static InstanceSettings Default { get; } = new(
        DefaultSchedulerIntervalSeconds,
        DefaultSchedulerBackfillDays,
        DefaultSchedulerMaxGenerationsPerTick,
        DefaultBackupEnabled,
        DefaultBackupLocalTime,
        DefaultAudioCleanupLocalTime,
        DefaultBackupKeepCount,
        RetrievalSettings.Default.MaxMaterials,
        RetrievalSettings.Default.CandidateScanLimit,
        RetrievalSettings.Default.MinimumRelevance,
        RetrievalSettings.Default.MinimumLexicalScore);

    public InstanceSettings(
        int schedulerIntervalSeconds,
        int schedulerBackfillWindowDays,
        int schedulerMaxGenerationsPerTick,
        bool backupEnabled,
        TimeOnly backupLocalTime,
        TimeOnly audioCleanupLocalTime,
        int backupKeepCount,
        int retrievalMaxMaterials,
        int retrievalCandidateScanLimit,
        double retrievalMinimumRelevance,
        double retrievalMinimumLexicalScore)
    {
        SchedulerIntervalSeconds = schedulerIntervalSeconds;
        SchedulerBackfillWindowDays = schedulerBackfillWindowDays;
        SchedulerMaxGenerationsPerTick = schedulerMaxGenerationsPerTick;
        BackupEnabled = backupEnabled;
        BackupLocalTime = backupLocalTime;
        AudioCleanupLocalTime = audioCleanupLocalTime;
        BackupKeepCount = backupKeepCount;
        RetrievalMaxMaterials = retrievalMaxMaterials;
        RetrievalCandidateScanLimit = retrievalCandidateScanLimit;
        RetrievalMinimumRelevance = retrievalMinimumRelevance;
        RetrievalMinimumLexicalScore = retrievalMinimumLexicalScore;
    }

    /// <summary>Local time in the content time zone, not the server's own zone.</summary>
    public TimeSpan SchedulerInterval => TimeSpan.FromSeconds(SchedulerIntervalSeconds);

    public int SchedulerIntervalSeconds { get; init; }

    public int SchedulerBackfillWindowDays { get; init; }

    public int SchedulerMaxGenerationsPerTick { get; init; }

    public bool BackupEnabled { get; init; }

    public TimeOnly BackupLocalTime { get; init; }

    public TimeOnly AudioCleanupLocalTime { get; init; }

    public int BackupKeepCount { get; init; }

    public int RetrievalMaxMaterials { get; init; }

    public int RetrievalCandidateScanLimit { get; init; }

    public double RetrievalMinimumRelevance { get; init; }

    public double RetrievalMinimumLexicalScore { get; init; }

    /// <summary>The retrieval half, in the shape the retrieval code already speaks.</summary>
    public RetrievalSettings ToRetrievalSettings() => new(
        RetrievalMaxMaterials,
        RetrievalCandidateScanLimit,
        RetrievalMinimumRelevance,
        RetrievalMinimumLexicalScore);

    /// <summary>Reads a snapshot out of persisted key/value pairs, falling back to the defaults.</summary>
    public static InstanceSettings FromValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var defaults = Default;

        return new InstanceSettings(
            ReadInt(values, SchedulerIntervalKey, defaults.SchedulerIntervalSeconds),
            ReadInt(values, SchedulerBackfillKey, defaults.SchedulerBackfillWindowDays),
            ReadInt(values, SchedulerMaxGenerationsKey, defaults.SchedulerMaxGenerationsPerTick),
            ReadBool(values, BackupEnabledKey, defaults.BackupEnabled),
            ReadTime(values, BackupLocalTimeKey, defaults.BackupLocalTime),
            ReadTime(values, AudioCleanupLocalTimeKey, defaults.AudioCleanupLocalTime),
            ReadInt(values, BackupKeepCountKey, defaults.BackupKeepCount),
            ReadInt(values, RetrievalMaxMaterialsKey, defaults.RetrievalMaxMaterials),
            ReadInt(values, RetrievalCandidateScanLimitKey, defaults.RetrievalCandidateScanLimit),
            ReadDouble(values, RetrievalMinimumRelevanceKey, defaults.RetrievalMinimumRelevance),
            ReadDouble(values, RetrievalMinimumLexicalScoreKey, defaults.RetrievalMinimumLexicalScore));
    }

    public IReadOnlyDictionary<string, string> ToValues() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchedulerIntervalKey] = SchedulerIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            [SchedulerBackfillKey] = SchedulerBackfillWindowDays.ToString(CultureInfo.InvariantCulture),
            [SchedulerMaxGenerationsKey] = SchedulerMaxGenerationsPerTick.ToString(CultureInfo.InvariantCulture),
            [BackupEnabledKey] = BackupEnabled ? "true" : "false",
            [BackupLocalTimeKey] = ContentSettings.FormatTime(BackupLocalTime),
            [AudioCleanupLocalTimeKey] = ContentSettings.FormatTime(AudioCleanupLocalTime),
            [BackupKeepCountKey] = BackupKeepCount.ToString(CultureInfo.InvariantCulture),
            [RetrievalMaxMaterialsKey] = RetrievalMaxMaterials.ToString(CultureInfo.InvariantCulture),
            [RetrievalCandidateScanLimitKey] = RetrievalCandidateScanLimit.ToString(CultureInfo.InvariantCulture),
            [RetrievalMinimumRelevanceKey] = RetrievalMinimumRelevance.ToString("0.####", CultureInfo.InvariantCulture),
            [RetrievalMinimumLexicalScoreKey] = RetrievalMinimumLexicalScore.ToString("0.####", CultureInfo.InvariantCulture),
        };

    private static string? Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        int.TryParse(Read(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        double.TryParse(Read(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        bool.TryParse(Read(values, key), out var parsed) ? parsed : fallback;

    private static TimeOnly ReadTime(IReadOnlyDictionary<string, string> values, string key, TimeOnly fallback) =>
        TimeOnly.TryParseExact(Read(values, key), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>Supplies the current operational settings; a fresh read per call keeps it consistent with the store.</summary>
public interface IInstanceSettingsProvider
{
    Task<InstanceSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Only the fields the caller wants to change. Absent means "leave it as it is".</summary>
public sealed record InstanceSettingsUpdate(
    int? SchedulerIntervalSeconds,
    int? SchedulerBackfillWindowDays,
    int? SchedulerMaxGenerationsPerTick,
    bool? BackupEnabled,
    TimeOnly? BackupLocalTime,
    TimeOnly? AudioCleanupLocalTime,
    int? BackupKeepCount,
    int? RetrievalMaxMaterials,
    int? RetrievalCandidateScanLimit,
    double? RetrievalMinimumRelevance,
    double? RetrievalMinimumLexicalScore);

/// <summary>
/// Changes the operational settings (docs/开发指导.md §4.1).
/// <para>
/// Validated in full before anything is written, and written in one transaction. The model and SMTP editors
/// validate and store field by field, which lets a rejected field leave the earlier ones already applied — an
/// operator sees an error and a half-changed instance. This one deliberately does not repeat that.
/// </para>
/// </summary>
public sealed class UpdateInstanceSettingsUseCase
{
    private readonly IAppSettingStore _settings;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateInstanceSettingsUseCase(IAppSettingStore settings, IUnitOfWork unitOfWork)
    {
        _settings = settings;
        _unitOfWork = unitOfWork;
    }

    public async Task<InstanceSettings> ExecuteAsync(
        InstanceSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = InstanceSettings.FromValues(
            await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false));

        var next = new InstanceSettings(
            update.SchedulerIntervalSeconds ?? current.SchedulerIntervalSeconds,
            update.SchedulerBackfillWindowDays ?? current.SchedulerBackfillWindowDays,
            update.SchedulerMaxGenerationsPerTick ?? current.SchedulerMaxGenerationsPerTick,
            update.BackupEnabled ?? current.BackupEnabled,
            update.BackupLocalTime ?? current.BackupLocalTime,
            update.AudioCleanupLocalTime ?? current.AudioCleanupLocalTime,
            update.BackupKeepCount ?? current.BackupKeepCount,
            update.RetrievalMaxMaterials ?? current.RetrievalMaxMaterials,
            update.RetrievalCandidateScanLimit ?? current.RetrievalCandidateScanLimit,
            update.RetrievalMinimumRelevance ?? current.RetrievalMinimumRelevance,
            update.RetrievalMinimumLexicalScore ?? current.RetrievalMinimumLexicalScore);

        Validate(next);

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in next.ToValues())
        {
            await _settings.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return next;
    }

    private static void Validate(InstanceSettings settings)
    {
        Require(
            settings.SchedulerIntervalSeconds is >= InstanceSettings.MinimumSchedulerIntervalSeconds
                and <= InstanceSettings.MaximumSchedulerIntervalSeconds,
            "instance.scheduler_interval.invalid",
            $"调度间隔需要介于 {InstanceSettings.MinimumSchedulerIntervalSeconds} 与 "
            + $"{InstanceSettings.MaximumSchedulerIntervalSeconds} 秒之间。");

        Require(
            settings.SchedulerBackfillWindowDays is >= InstanceSettings.MinimumBackfillDays
                and <= InstanceSettings.MaximumBackfillDays,
            "instance.scheduler_backfill.invalid",
            $"补跑天数需要介于 {InstanceSettings.MinimumBackfillDays} 与 {InstanceSettings.MaximumBackfillDays} 之间。");

        Require(
            settings.SchedulerMaxGenerationsPerTick is >= 1 and <= InstanceSettings.MaximumGenerationsPerTick,
            "instance.scheduler_batch.invalid",
            $"每次调度的生成上限需要介于 1 与 {InstanceSettings.MaximumGenerationsPerTick} 之间。");

        Require(
            settings.BackupKeepCount is >= InstanceSettings.MinimumBackupKeepCount
                and <= InstanceSettings.MaximumBackupKeepCount,
            "instance.backup_keep.invalid",
            $"备份保留份数需要介于 {InstanceSettings.MinimumBackupKeepCount} 与 "
            + $"{InstanceSettings.MaximumBackupKeepCount} 之间。");

        Require(
            settings.RetrievalMaxMaterials is >= 1 and <= InstanceSettings.MaximumRetrievalMaterials,
            "instance.retrieval_materials.invalid",
            $"检索返回条数需要介于 1 与 {InstanceSettings.MaximumRetrievalMaterials} 之间。");

        Require(
            settings.RetrievalCandidateScanLimit is >= 1 and <= InstanceSettings.MaximumCandidateScanLimit,
            "instance.retrieval_scan.invalid",
            $"候选扫描上限需要介于 1 与 {InstanceSettings.MaximumCandidateScanLimit} 之间。");

        Require(IsRatio(settings.RetrievalMinimumRelevance), "instance.retrieval_relevance.invalid", "相关性下限需要介于 0 与 1 之间。");
        Require(IsRatio(settings.RetrievalMinimumLexicalScore), "instance.retrieval_lexical.invalid", "词面匹配下限需要介于 0 与 1 之间。");
    }

    private static bool IsRatio(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static void Require(bool condition, string code, string message)
    {
        if (!condition)
        {
            throw new UseCaseException(code, message);
        }
    }
}
