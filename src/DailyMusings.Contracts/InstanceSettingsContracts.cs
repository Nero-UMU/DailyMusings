namespace DailyMusings.Contracts;

/// <summary>
/// The instance's operational settings (docs/开发指导.md §7, §8.3, §15.1, §15.2), as the admin page reads them.
/// <para>
/// Times travel as <c>HH:mm</c> strings in the content time zone, the same way the content settings do, so the page
/// never has to guess a server locale.
/// </para>
/// </summary>
public sealed record InstanceSettingsDto(
    int SchedulerIntervalSeconds,
    int SchedulerBackfillWindowDays,
    int SchedulerMaxGenerationsPerTick,
    bool BackupEnabled,
    string BackupLocalTime,
    string AudioCleanupLocalTime,
    string ContentCleanupLocalTime,
    int BackupKeepCount,
    int RetrievalMaxMaterials,
    int RetrievalCandidateScanLimit,
    double RetrievalMinimumRelevance,
    double RetrievalMinimumLexicalScore,
    int InlineTranscriptionTimeoutSeconds);

/// <summary>Every field is optional: absent means "leave it as it is".</summary>
public sealed record UpdateInstanceSettingsRequest(
    int? SchedulerIntervalSeconds,
    int? SchedulerBackfillWindowDays,
    int? SchedulerMaxGenerationsPerTick,
    bool? BackupEnabled,
    string? BackupLocalTime,
    string? AudioCleanupLocalTime,
    int? BackupKeepCount,
    int? RetrievalMaxMaterials,
    int? RetrievalCandidateScanLimit,
    double? RetrievalMinimumRelevance,
    double? RetrievalMinimumLexicalScore,
    string? ContentCleanupLocalTime = null,
    int? InlineTranscriptionTimeoutSeconds = null);

/// <summary>
/// The listening port, which is the one setting that cannot live in the settings table (§8.1).
/// <para>
/// <see cref="EffectivePort"/> is the port this very request arrived on, which is the only honest answer to "what
/// is it using right now". <see cref="RestartRequired"/> is true when a saved override differs from it: a
/// container's published port mapping is fixed when the container starts, so the change needs a recreate, and the
/// page says so instead of pretending the new port is already live.
/// </para>
/// <para>
/// <see cref="Locked"/> is true when the <em>deployment</em> owns the port — the shipped Compose file sets
/// <c>DAILYMUSINGS_LOCK_LISTENING_PORT=1</c>. Then there is nothing to save, nothing pending, and one place to
/// change it: the deployment's own port setting. A page that still offered the edit would be offering a way to
/// make the instance unreachable.
/// </para>
/// </summary>
public sealed record ListeningPortDto(
    int? OverridePort,
    int EffectivePort,
    bool RestartRequired,
    bool Locked,
    string? UpdatedBy,
    string? UpdatedAtUtc,
    string RuntimeConfigPath,
    string RootPath,
    string SecretsPath,
    string KeyRingPath);

/// <summary>Sets the listening port, or clears the override when <see cref="Port"/> is <c>null</c>.</summary>
public sealed record UpdateListeningPortRequest(int? Port);
