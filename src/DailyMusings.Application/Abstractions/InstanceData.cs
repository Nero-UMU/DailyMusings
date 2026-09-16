using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Abstractions;

/// <summary>One text file that belongs in an export or a backup.</summary>
public sealed record ExportedFile(string RelativePath, string Content);

/// <summary>A stored blob to copy, given as the stored path plus where it should land in the package.</summary>
public sealed record ExportedBlob(string StoredPath, string RelativePath);

/// <summary>What an export writes, already turned into files by the use case.</summary>
public sealed record InstanceExportRequest(
    string Stamp,
    IReadOnlyList<ExportedFile> Files,
    IReadOnlyList<ExportedBlob> Blobs,

    /// <summary>
    /// Audio paths that were expected but no longer exist. Reported rather than ignored: a missing recording is
    /// either a retention sweep that already ran or a lost file, and the manifest should say which it was.
    /// </summary>
    IReadOnlyList<string> MissingBlobs);

public sealed record ExportedEntry(string RelativePath, long ByteCount, string Sha256);

public sealed record InstanceExportResult(
    string RelativeRoot,
    IReadOnlyList<ExportedEntry> Entries,
    long TotalBytes,
    int CopiedBlobs);

/// <summary>A past export, as the admin page sees it.</summary>
public sealed record InstanceExportSummary(
    string RelativeRoot,
    DateTimeOffset CreatedAtUtc,
    int FileCount,
    long TotalBytes,
    string RetentionPolicy);

/// <summary>
/// Writes a readable export into the instance's export directory (docs/开发指导.md §15.1).
/// <para>
/// A port rather than file access in the use case, because §5 keeps the directory layout in one place: the use case
/// decides <em>what</em> a readable export contains, and the adapter decides where the instance keeps it.
/// </para>
/// </summary>
public interface IInstanceExportWriter
{
    Task<InstanceExportResult> WriteAsync(InstanceExportRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceExportSummary>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>What a backup contains, beyond the database snapshot the writer takes itself.</summary>
public sealed record BackupContent(
    IReadOnlyList<ExportedFile> Files,
    IReadOnlyList<ExportedBlob> Blobs,
    string ManifestJson,
    string RetentionPolicy);

public sealed record BackupSummary(
    string FileName,
    string RelativePath,
    DateTimeOffset CreatedAtUtc,
    long ByteCount,
    string Sha256,

    /// <summary>How many device tokens the archive had to remove while taking its database snapshot (A.13).</summary>
    int RedactedDeviceTokens,
    string RetentionPolicy);

/// <summary>
/// Takes a complete backup (docs/开发指导.md §15.2).
/// <para>
/// The database snapshot is the adapter's job rather than the use case's, for one reason worth stating: the copy has
/// to be <em>sanitized</em> — device tokens are removed from it (A.13) — and doing that means reading and writing
/// SQLite, which no use case should be doing. What the use case owns is the content, the manifest and the policy.
/// </para>
/// </summary>
public interface IBackupWriter
{
    Task<BackupSummary> WriteAsync(BackupContent content, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Deletes all but the newest <paramref name="keep"/> archives. Returns how many were removed.</summary>
    Task<int> PruneAsync(int keep, CancellationToken cancellationToken);
}

/// <summary>The verdict on an uploaded backup, before anything is applied.</summary>
public sealed record RestoreValidation(
    bool IsValid,
    string? Code,
    string? Detail,
    IReadOnlyList<string> Contents,
    string? SchemaVersion,
    string? CreatedAtUtc)
{
    public static RestoreValidation Rejected(string code, string detail) => new(false, code, detail, [], null, null);
}

/// <summary>
/// Validates an uploaded backup and stages it to be applied at the next start (docs/开发指导.md §15.2 step 3).
/// <para>
/// Staged rather than applied in place, and that is a deliberate reading of the guide. Replacing a live SQLite
/// database under a running process — with pooled handles, a write-ahead log and jobs mid-transaction — has no
/// honest failure story: a half-replaced instance is worse than one that has not restored yet. Staging validates
/// the archive, says exactly what it contains, and lets the operator restart into the restored state.
/// </para>
/// </summary>
public interface IRestoreStager
{
    Task<RestoreValidation> StageAsync(Stream archive, CancellationToken cancellationToken);

    /// <summary>Whether a staged restore is waiting to be applied.</summary>
    Task<RestoreValidation?> GetPendingAsync(CancellationToken cancellationToken);
}

/// <summary>What an export or a backup needs from the outside world to describe itself.</summary>
public sealed record InstanceDataContext(
    ContentDate Today,
    string? SchemaVersion,
    string RetentionPolicy,
    IReadOnlyList<string> RedactionNotes);
