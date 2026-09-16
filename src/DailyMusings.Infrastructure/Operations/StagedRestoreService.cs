using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// Validates an uploaded backup and stages it to be applied at the next start
/// (docs/开发指导.md §15.2 step 3).
/// <para>
/// Nothing here touches live state. The archive is checked, and then applied by
/// <see cref="StagedRestoreStartupTask"/> before the instance serves anything — because replacing a live SQLite
/// database under a running process, with pooled handles and jobs mid-transaction, has no honest partial-failure
/// story.
/// </para>
/// </summary>
public sealed class StagedRestoreService : IRestoreStager
{
    internal const string PendingFileName = "restore-pending.json";
    internal const string StagingDirectoryName = "restore-staging";
    internal const string DatabaseEntryName = "dailymusings.db";
    internal const string ManifestEntryName = "manifest.json";

    /// <summary>Refuses an archive big enough to be something other than a backup of a personal instance.</summary>
    public const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;

    private readonly InstancePaths _paths;
    private readonly ILogger<StagedRestoreService> _logger;

    public StagedRestoreService(InstancePaths paths, ILogger<StagedRestoreService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string StagingPath => Path.Combine(_paths.DataDirectory, StagingDirectoryName);

    private string PendingPath => Path.Combine(_paths.DataDirectory, PendingFileName);

    public async Task<RestoreValidation> StageAsync(Stream archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var staging = StagingPath;

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        var archivePath = Path.Combine(staging, "upload.zip");

        long total = 0;

        await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                if (total > MaxArchiveBytes)
                {
                    Directory.Delete(staging, recursive: true);
                    return RestoreValidation.Rejected(
                        "restore.archive.too_large",
                        "The uploaded archive is larger than this instance will restore.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (total == 0)
        {
            Directory.Delete(staging, recursive: true);
            return RestoreValidation.Rejected("restore.archive.empty", "The uploaded file is empty.");
        }

        RestoreValidation validation;

        try
        {
            validation = await ExtractAndValidateAsync(archivePath, staging, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            Directory.Delete(staging, recursive: true);
            return RestoreValidation.Rejected(
                "restore.archive.unreadable",
                $"The uploaded file is not a readable backup archive ({exception.GetType().Name}).");
        }

        if (!validation.IsValid)
        {
            Directory.Delete(staging, recursive: true);
            return validation;
        }

        await File.WriteAllTextAsync(
            PendingPath,
            JsonSerializer.Serialize(new
            {
                stagedAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                contents = validation.Contents,
                schemaVersion = validation.SchemaVersion,
                createdAtUtc = validation.CreatedAtUtc,
            }),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "A restore is staged and will be applied at the next start ({EntryCount} entries).",
            validation.Contents.Count);

        return validation;
    }

    public async Task<RestoreValidation?> GetPendingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PendingPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(PendingPath, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;

            var contents = root.TryGetProperty("contents", out var list)
                ? list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                : [];

            return new RestoreValidation(
                true,
                null,
                "已暂存，重启实例后生效。",
                contents,
                root.TryGetProperty("schemaVersion", out var schema) ? schema.GetString() : null,
                root.TryGetProperty("createdAtUtc", out var created) ? created.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<RestoreValidation> ExtractAndValidateAsync(
        string archivePath,
        string staging,
        CancellationToken cancellationToken)
    {
        var extractRoot = Path.Combine(staging, "payload");
        Directory.CreateDirectory(extractRoot);

        using var archive = ZipFile.OpenRead(archivePath);

        var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();

        if (!names.Contains(DatabaseEntryName, StringComparer.Ordinal))
        {
            return RestoreValidation.Rejected(
                "restore.archive.not_a_backup",
                "The archive does not contain a database snapshot, so it is not a backup of this product.");
        }

        string? createdAt = null;
        string? schema = null;

        if (archive.GetEntry(ManifestEntryName) is { } manifestEntry)
        {
            using var reader = new StreamReader(manifestEntry.Open());
            using var document = JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));

            createdAt = document.RootElement.TryGetProperty("createdAtUtc", out var created) ? created.GetString() : null;
            schema = document.RootElement.TryGetProperty("schemaVersion", out var version) ? version.GetString() : null;
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');

            // Zip-slip: an entry whose path climbs out of the extraction directory would write anywhere the process
            // can reach. Refused rather than normalized, because a backup never legitimately contains such a name.
            if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name))
            {
                return RestoreValidation.Rejected(
                    "restore.archive.unsafe_entry",
                    "The archive contains an entry that would be written outside the instance.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destination = Path.Combine(extractRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        var databasePath = Path.Combine(extractRoot, DatabaseEntryName);

        var downgrade = await CheckSchemaAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (downgrade is not null)
        {
            return downgrade;
        }

        // §15.2 step 8: a restore brings back content, not credentials. Checking rather than claiming is what makes
        // the promise checkable.
        var devices = await CountAsync(databasePath, "SELECT COUNT(*) FROM device;", cancellationToken).ConfigureAwait(false);
        if (devices > 0)
        {
            return RestoreValidation.Rejected(
                "restore.archive.contains_device_tokens",
                $"The archive contains {devices} device credential(s); a backup must not carry them (A.13).");
        }

        return new RestoreValidation(true, null, null, names, schema, createdAt);
    }

    /// <summary>
    /// Refuses a backup taken by a newer build. §15.3 promises forward migration only, so a downgrade would leave
    /// the instance with a schema it cannot reason about — and the honest moment to say so is before anything is
    /// replaced, not after a failed start.
    /// </summary>
    private static async Task<RestoreValidation?> CheckSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(AppContext.BaseDirectory);
        _ = path;

        // Pooling off for the same reason as the snapshot: these handles must be gone before the files are moved.
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var applied = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT migration_id FROM schema_migrations ORDER BY migration_id;";

            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetString(0));
                }
            }
            catch (SqliteException)
            {
                return RestoreValidation.Rejected(
                    "restore.archive.no_schema_migrations",
                    "The database in the archive does not record any migrations, so it was not created by this product.");
            }
        }

        var known = EmbeddedMigrations.Ids;

        var unknown = applied.Where(id => !known.Contains(id, StringComparer.Ordinal)).ToArray();

        return unknown.Length == 0
            ? null
            : RestoreValidation.Rejected(
                "restore.archive.from_newer_version",
                $"The archive was written by a newer version of this instance (unknown migration {unknown[0]}). "
                + "Restore it with that version; downgrades are not supported (§15.3).");
    }

    private static async Task<long> CountAsync(string databasePath, string sql, CancellationToken cancellationToken)
    {
        // Pooling off for the same reason as the snapshot: these handles must be gone before the files are moved.
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>The migration identifiers this build embeds, so a staged archive can be checked against them.</summary>
internal static class EmbeddedMigrations
{
    public static IReadOnlyList<string> Ids { get; } = typeof(EmbeddedMigrations).Assembly
        .GetManifestResourceNames()
        .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
        .Select(name =>
        {
            var trimmed = name[..^4];
            return trimmed[(trimmed.LastIndexOf('.') + 1)..];
        })
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();
}
