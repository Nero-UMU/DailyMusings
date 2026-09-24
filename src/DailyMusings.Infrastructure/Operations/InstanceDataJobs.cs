using System.Globalization;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Operations;
using DailyMusings.Domain.Jobs;
using DailyMusings.Infrastructure.Jobs;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// Applies a staged restore before the instance does anything else (docs/开发指导.md §15.2 step 3).
/// <para>
/// This runs ahead of migrations and ahead of the web host, for the same reason migrations do: an instance must never
/// serve a request against a half-replaced state. It is invoked from the composition root rather than registered as a
/// hosted service, so "restored" is true before the first request arrives.
/// </para>
/// </summary>
public static class StagedRestoreStartupTask
{
    /// <summary>Returns a description of what was applied, or <c>null</c> when there was nothing to do.</summary>
    public static async Task<string?> ApplyAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var paths = InstancePaths.FromConfiguration(configuration);

        var pending = Path.Combine(paths.DataDirectory, StagedRestoreService.PendingFileName);
        var payload = Path.Combine(paths.DataDirectory, StagedRestoreService.StagingDirectoryName, "payload");
        var stagedDatabase = Path.Combine(payload, StagedRestoreService.DatabaseEntryName);

        if (!File.Exists(pending) || !File.Exists(stagedDatabase))
        {
            return null;
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        // The current state is kept, not discarded. If the archive turns out to be the wrong one, the operator has
        // something to go back to — and a restore that silently destroys what it replaced would be the single most
        // dangerous operation in the product.
        var preserved = Path.Combine(paths.DataDirectory, $"pre-restore-{stamp}.db");
        if (File.Exists(paths.DatabasePath))
        {
            File.Copy(paths.DatabasePath, preserved, overwrite: true);
        }

        ReplaceDatabase(paths, stagedDatabase);
        var media = CopyTree(payload, "media", paths.MediaPath);
        var markdown = CopyTree(payload, "markdown", paths.MarkdownPath);

        Directory.Delete(Path.Combine(paths.DataDirectory, StagedRestoreService.StagingDirectoryName), recursive: true);
        File.Delete(pending);

        var description =
            $"Restored a staged backup: {media} recording(s) and {markdown} markdown file(s) copied. "
            + $"The previous database was kept as {Path.GetFileName(preserved)}. Devices must be paired again.";

        logger.LogInformation("{Description}", description);

        return description;
    }

    private static void ReplaceDatabase(InstancePaths paths, string stagedDatabase)
    {
        // The write-ahead log and shared-memory files belong to the database being replaced. Leaving them behind
        // would let SQLite recover the old content over the new file.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = paths.DatabasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        File.Copy(stagedDatabase, paths.DatabasePath, overwrite: true);
    }

    /// <summary>
    /// Copies a subtree of the archive over the instance's own.
    /// <para>
    /// A union rather than a wipe: the archived files win, and anything the archive does not mention is left alone.
    /// Deleting first would be tidier, but it would also mean a partial archive silently removes recordings that the
    /// user never deleted — and unreferenced leftovers are the harmless direction to fail in.
    /// </para>
    /// </summary>
    private static int CopyTree(string payload, string folder, string destination)
    {
        var source = Path.Combine(payload, folder);

        if (!Directory.Exists(source))
        {
            return 0;
        }

        Directory.CreateDirectory(destination);
        var copied = 0;

        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path);
            var target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: true);
            copied++;
        }

        return copied;
    }
}

/// <summary>
/// Runs the retention sweep for recordings (docs/开发指导.md §15.1, decision A.1).
/// <para>
/// Counts only, and no content: §16 keeps recordings and transcripts out of the log, and a sweep is exactly the
/// place where it would be tempting to name files.
/// </para>
/// </summary>
public sealed class AudioCleanupJobHandler : IJobHandler
{
    private readonly RunAudioCleanupUseCase _cleanup;
    private readonly ILogger<AudioCleanupJobHandler> _logger;

    public AudioCleanupJobHandler(RunAudioCleanupUseCase cleanup, ILogger<AudioCleanupJobHandler> logger)
    {
        _cleanup = cleanup;
        _logger = logger;
    }

    public JobType JobType => JobType.AudioCleanup;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var result = await _cleanup.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (result.DeletedBlobs == 0 && result.FailedDeletions == 0)
        {
            return JobOutcome.Skipped;
        }

        _logger.LogInformation(
            "Audio retention removed {DeletedCount} recording(s) ({ReleasedBytes} bytes) across {DayCount} day(s).",
            result.DeletedBlobs,
            result.ReleasedBytes,
            result.CandidateDays);

        if (result.FailedDeletions > 0)
        {
            // The entries still name their blobs, so the next sweep tries again; nothing is lost track of.
            _logger.LogWarning("{FailedCount} recording(s) could not be deleted and will be retried.", result.FailedDeletions);
        }

        return JobOutcome.Completed;
    }
}

/// <summary>
/// Runs the retention sweep for captured content (the window added with this feature).
/// <para>
/// The same shape as the audio sweep, and separate from it on purpose: the two windows answer different
/// questions ("how long do I keep the recording so I can check the transcript" versus "how long do I keep what
/// I said"), and an operator who wants a short content window usually still wants the audio for a while.
/// </para>
/// </summary>
public sealed class ContentCleanupJobHandler : IJobHandler
{
    private readonly RunContentCleanupUseCase _cleanup;
    private readonly ILogger<ContentCleanupJobHandler> _logger;

    public ContentCleanupJobHandler(RunContentCleanupUseCase cleanup, ILogger<ContentCleanupJobHandler> logger)
    {
        _cleanup = cleanup;
        _logger = logger;
    }

    public JobType JobType => JobType.ContentCleanup;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var result = await _cleanup.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (result.CleanedEntries == 0 && result.FailedDeletions == 0)
        {
            // Includes the ordinary case of the window being off ("keep forever"): a sweep that has nothing to
            // do is a success, and the queue must not accumulate failures for it.
            return JobOutcome.Skipped;
        }

        // Counts only — never a transcript, a file name or a topic (§16).
        _logger.LogInformation(
            "Content retention cleared {CleanedCount} entr(ies) ({ReleasedBytes} bytes) across {DayCount} day(s).",
            result.CleanedEntries,
            result.ReleasedBytes,
            result.CandidateDays);

        if (result.FailedDeletions > 0)
        {
            _logger.LogWarning("{FailedCount} recording(s) could not be deleted; the entries were kept.", result.FailedDeletions);
        }

        return JobOutcome.Completed;
    }
}

/// <summary>
/// Takes a complete backup (docs/开发指导.md §15.2).
/// <para>
/// The daily schedule and the one-click admin action both arrive here, which is why the job carries no parameters:
/// a backup is the same operation whichever way it was asked for.
/// </para>
/// </summary>
public sealed class BackupJobHandler : IJobHandler
{
    private readonly CreateBackupUseCase _backup;
    private readonly ILogger<BackupJobHandler> _logger;

    public BackupJobHandler(CreateBackupUseCase backup, ILogger<BackupJobHandler> logger)
    {
        _backup = backup;
        _logger = logger;
    }

    public JobType JobType => JobType.Backup;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var summary = await _backup.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        // The file name and the size: never its contents, and never the count of credentials it had to redact
        // beyond what the writer already logged.
        _logger.LogInformation(
            "Backup {FileName} written ({ByteCount} bytes).",
            summary.FileName,
            summary.ByteCount);

        return JobOutcome.Completed;
    }
}
