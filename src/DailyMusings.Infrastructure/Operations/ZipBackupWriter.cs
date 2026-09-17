using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// Takes the database half of a backup (docs/开发指导.md §15.2, decisions A.13 and A.14).
/// <para>
/// The copy is taken with SQLite's own <c>VACUUM INTO</c>, which produces a consistent, compact snapshot without
/// stopping the writer — copying the file would risk capturing a torn state, and copying the write-ahead log's
/// contents by hand is not something to reimplement.
/// </para>
/// <para>
/// Then the credential material is removed, which is the part A.13 is about. Device tokens are deleted
/// <em>and</em> the entries that referenced those devices are detached first: the column is a foreign key, and
/// leaving it pointing at rows that are about to vanish would make the restored database refuse to open. Detaching
/// is also what the decision says happens in practice — after a restore nothing is paired, so "which device captured
/// this" is no longer knowable.
/// </para>
/// </summary>
public sealed class SqliteDatabaseSnapshotter
{
    private readonly SqliteConnectionAccessor _accessor;
    private readonly ILogger<SqliteDatabaseSnapshotter> _logger;

    public SqliteDatabaseSnapshotter(SqliteConnectionAccessor accessor, ILogger<SqliteDatabaseSnapshotter> logger)
    {
        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>Writes a sanitized snapshot to <paramref name="destinationPath"/> and reports what it removed.</summary>
    public async Task<int> CreateAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var connection = await _accessor.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        // VACUUM INTO refuses to overwrite, and its argument is a string literal rather than a bound parameter, so
        // the path is escaped rather than concatenated.
        var escaped = destinationPath.Replace("'", "''", StringComparison.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"VACUUM INTO '{escaped}';";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var redacted = await SanitizeAsync(destinationPath, cancellationToken).ConfigureAwait(false);

        if (redacted > 0)
        {
            // Only the count: §16 keeps content and credentials out of the log, and a token is a credential.
            _logger.LogInformation(
                "Backup snapshot removed {RedactedCount} device credential(s); the devices must be paired again after a restore.",
                redacted);
        }

        return redacted;
    }

    private static async Task<int> SanitizeAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,

            // Pooling off, deliberately. The default pool keeps the file handle open after Dispose returns, and this
            // connection exists only to sanitize a snapshot that is then copied into an archive — on Windows the
            // still-open handle makes that copy fail with "being used by another process".
            Pooling = false,
        };

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Foreign keys are off in the snapshot by default (SQLite's default), which is why the detach below is
        // ordered deliberately rather than relied upon to cascade.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var paired = await ExecuteAsync(connection, "SELECT COUNT(*) FROM device;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "UPDATE input_entry SET device_id = NULL WHERE device_id IS NOT NULL;", cancellationToken)
            .ConfigureAwait(false);

        // Pairing codes are ten-minute single-use credentials; a backup carrying one would be a backup carrying a
        // way in for however long was left on it.
        await ExecuteAsync(connection, "DELETE FROM pairing_code;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM device;", cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The deletes above leave the removed rows' bytes in pages the file no longer uses, so a `grep` over a backup
        // still found a token digest — found by the phase-five restore verification, which scans the restored
        // instance for exactly that. The digest is not a credential (the row that would accept it is gone), but
        // "this backup contains no device credential" has to survive being checked rather than being argued about,
        // so the file is rebuilt. VACUUM cannot run inside a transaction, which is why it comes after the commit.
        await ExecuteAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);

        return paired;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture)
            : await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes a complete backup archive and keeps the last few (docs/开发指导.md §15.2).
/// <para>
/// The archive is a zip of the whole state — database, media, Markdown output, the readable data files and the
/// manifest — plus nothing else. The DataProtection key ring is not copied because it never enters the staging
/// directory: it lives outside the instance root by design (A.14), so its exclusion is structural rather than a rule
/// somebody has to remember.
/// </para>
/// </summary>
public sealed class ZipBackupWriter : IBackupWriter
{
    private const string DatabaseEntryName = "dailymusings.db";
    private const string ManifestEntryName = "manifest.json";

    private readonly InstancePaths _paths;
    private readonly SqliteDatabaseSnapshotter _snapshotter;
    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore? _storedSettings;
    private readonly ILogger<ZipBackupWriter> _logger;

    /// <summary>
    /// <paramref name="storedSettings"/> is optional and last on purpose: the composition root supplies the scoped
    /// store (so the retention count is admin-editable, §8.1), while a test that only cares about what lands in the
    /// archive can keep constructing the writer with the four arguments that describe the filesystem and the
    /// configuration. When it is absent the retention count falls back to the deployment configuration.
    /// </summary>
    public ZipBackupWriter(
        InstancePaths paths,
        SqliteDatabaseSnapshotter snapshotter,
        IConfiguration configuration,
        ILogger<ZipBackupWriter> logger,
        IAppSettingStore? storedSettings = null)
    {
        _paths = paths;
        _snapshotter = snapshotter;
        _configuration = configuration;
        _logger = logger;
        _storedSettings = storedSettings;
    }

    public async Task<BackupSummary> WriteAsync(
        BackupContent content,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = ResolveFreeFileName(stamp);
        var archivePath = Path.Combine(_paths.BackupPath, fileName);
        var staging = Path.Combine(_paths.BackupPath, $".staging-{Path.GetFileNameWithoutExtension(fileName)}");

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        try
        {
            var redacted = await _snapshotter
                .CreateAsync(Path.Combine(staging, DatabaseEntryName), cancellationToken)
                .ConfigureAwait(false);

            foreach (var file in content.Files)
            {
                var path = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                await File.WriteAllBytesAsync(
                    path,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(file.Content),
                    cancellationToken).ConfigureAwait(false);
            }

            var blobs = 0;

            foreach (var blob in content.Blobs)
            {
                var source = _paths.ResolveMediaFile(blob.StoredPath);
                if (!File.Exists(source))
                {
                    continue;
                }

                var destination = Path.Combine(staging, blob.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                blobs++;
            }

            await File.WriteAllTextAsync(
                Path.Combine(staging, ManifestEntryName),
                content.ManifestJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            await ZipAsync(staging, archivePath, cancellationToken).ConfigureAwait(false);
            _ = blobs;

            var info = new FileInfo(archivePath);
            var hash = await FileInstanceExportWriter.HashFileAsync(archivePath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Backup written ({ByteCount} bytes).", info.Length);

            return new BackupSummary(
                fileName,
                fileName,
                nowUtc,
                info.Length,
                hash,
                redacted,
                content.RetentionPolicy);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// An archive name for this run. The stamp has one-second resolution, so two backups can ask for the same name;
    /// the second gets a suffix rather than replacing the first. Silently dropping a copy is the one thing a feature
    /// whose whole point is "the last seven copies" must not do, and the staging directory follows the same name so
    /// two runs cannot share it either.
    /// </summary>
    private string ResolveFreeFileName(string stamp)
    {
        var candidate = $"{PackageFileName.BackupPrefix}{stamp}.zip";
        var suffix = 1;

        while (File.Exists(Path.Combine(_paths.BackupPath, candidate))
               || Directory.Exists(Path.Combine(_paths.BackupPath, $".staging-{Path.GetFileNameWithoutExtension(candidate)}")))
        {
            suffix++;
            candidate = $"{PackageFileName.BackupPrefix}{stamp}-{suffix}.zip";

            if (suffix > 100)
            {
                candidate = $"{PackageFileName.BackupPrefix}{stamp}-{Guid.NewGuid():N}.zip";
                break;
            }
        }

        return candidate;
    }

    public Task<IReadOnlyList<BackupSummary>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.BackupPath))
        {
            return Task.FromResult<IReadOnlyList<BackupSummary>>([]);
        }

        var summaries = new List<BackupSummary>();

        foreach (var path in Directory.EnumerateFiles(_paths.BackupPath, PackageFileName.BackupPrefix + "*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            summaries.Add(new BackupSummary(
                info.Name,
                info.Name,
                info.CreationTimeUtc,
                info.Length,
                string.Empty,
                RedactedDeviceTokens: 0,
                RetentionPolicy: "(见包内清单)"));
        }

        return Task.FromResult<IReadOnlyList<BackupSummary>>(
            PackageFileName.NewestFirst(summaries, summary => summary.FileName).ToArray());
    }

    /// <summary>
    /// Keeps the newest N archives. §15.2 asks for seven by default.
    /// <para>
    /// Three layers, most specific first: what the admin page saved, then <c>Backup:KeepCount</c> from the
    /// deployment configuration, then the count the caller asked for. The stored value is read raw rather than
    /// through <see cref="InstanceSettings"/> precisely so that "never configured" stays distinguishable from
    /// "configured as the default" — otherwise the caller's argument could never win.
    /// </para>
    /// </summary>
    public async Task<int> PruneAsync(int keep, CancellationToken cancellationToken)
    {
        var effective = Math.Max(1, _configuration.GetValue("Backup:KeepCount", keep));

        // The admin page's value wins when it is present. Read raw rather than through InstanceSettings precisely so
        // that "never configured" stays distinguishable from "configured as the default" — otherwise the caller's
        // argument could never win.
        if (_storedSettings is not null)
        {
            var stored = await _storedSettings.GetAllAsync(cancellationToken).ConfigureAwait(false);

            if (stored.TryGetValue(InstanceSettings.BackupKeepCountKey, out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var storedKeep))
            {
                effective = Math.Max(1, storedKeep);
            }
        }

        if (!Directory.Exists(_paths.BackupPath))
        {
            return 0;
        }

        var archives = PackageFileName
            .NewestFirst(
                Directory.EnumerateFiles(_paths.BackupPath, PackageFileName.BackupPrefix + "*.zip").Select(path => new FileInfo(path)),
                info => info.Name)
            .ToArray();

        var removed = 0;

        foreach (var archive in archives.Skip(effective))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                archive.Delete();
                removed++;
            }
            catch (IOException exception)
            {
                // Reported, not fatal: keeping one archive too many is the harmless direction.
                _logger.LogWarning(
                    "Could not remove an old backup: {ErrorType}.",
                    exception.GetType().Name);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {RemovedCount} old backup(s), keeping {KeepCount}.", removed, effective);
        }

        return removed;
    }

    private static async Task ZipAsync(string directory, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        // ZipFile.CreateFromDirectory would be shorter, but it gives no way to report progress or to keep the entry
        // names slash-separated across platforms, which matters for an archive restored on another machine.
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryName = Path.GetFullPath(path)[root.Length..].Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            await using var entryStream = entry.Open();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
