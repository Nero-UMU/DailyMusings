namespace DailyMusings.Contracts;

/// <summary>One export package on disk (docs/开发指导.md §15.1).</summary>
public sealed record InstanceExportDto(
    string RelativeRoot,
    string CreatedAtUtc,
    int FileCount,
    long TotalBytes,

    /// <summary>The retention policy that was in force when it was taken; §15.1 wants the export to state it.</summary>
    string RetentionPolicy);

public sealed record InstanceExportListResponse(IReadOnlyList<InstanceExportDto> Items);

/// <summary>One backup archive on disk (docs/开发指导.md §15.2).</summary>
public sealed record BackupDto(
    string FileName,
    string CreatedAtUtc,
    long ByteCount,

    /// <summary>Empty when the archive was listed rather than just written; hashing every archive on every page load
    /// would read the whole backup set to draw a table.</summary>
    string Sha256,

    /// <summary>How many device credentials were stripped while taking the snapshot (A.13).</summary>
    int RedactedDeviceTokens,
    string RetentionPolicy);

public sealed record BackupListResponse(IReadOnlyList<BackupDto> Items);

/// <summary>
/// An upload of a backup archive to restore. The verdict says what is inside before anything is applied.
/// </summary>
public sealed record RestoreStatusResponse(
    bool Pending,
    bool Valid,
    string? Code,
    string? Detail,
    IReadOnlyList<string> Contents,
    string? SchemaVersion,
    string? CreatedAtUtc);

/// <summary>The index's state, so an operator can see whether semantic search is actually available (§8.3).</summary>
public sealed record IndexStatusDto(
    string? IndexedVersion,
    string? ConfiguredVersion,
    int IndexedEntries,
    bool SemanticSearchAvailable,
    bool RebuildInProgress);

/// <summary>
/// The temporary debug switch (§16). <c>ExpiresAtUtc</c> is what makes it temporary: the mode ends on its own,
/// whether or not anybody remembers to end it.
/// </summary>
public sealed record DiagnosticModeDto(bool Enabled, string? ExpiresAtUtc, string? EnabledBy, int RemainingMinutes);

/// <summary>
/// Turning the debug mode on. The acknowledgement is required and is not decoration: while it is on, transcriptions,
/// prompts and model responses may reach the log, and §16 requires the operator to be told that before it happens.
/// </summary>
public sealed record EnableDiagnosticModeRequest(int DurationMinutes, bool AcknowledgedContentRisk);

/// <summary>The result of a test connection (§16). <c>Code</c> is a stable identifier, never the remote's message.</summary>
public sealed record ProbeResultDto(string Service, bool Ok, string Code, string Detail);

/// <summary>What a maintenance run did, for the admin page's "run it again" buttons (§14).</summary>
public sealed record MaintenanceRunResponse(string JobType, string JobId, string Status, string ScheduledAtUtc);

/// <summary>What an audio cleanup sweep did. Counts only: never a file name or a transcript (§16).</summary>
public sealed record AudioCleanupResponse(int CandidateDays, int DeletedBlobs, int FailedDeletions, long ReleasedBytes);
