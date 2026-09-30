using System.IO.Compression;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Operations;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Operations;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The operations of docs/开发指导.md §15: a readable export, a complete backup, the staged restore and the recording
/// retention sweep.
/// <para>
/// Tested against a real instance directory and a real database, because almost every promise here is about what is
/// inside a package and what is deliberately not: "the backup contains no device tokens" is only a fact if the
/// archive is opened and looked into.
/// </para>
/// </summary>
[TestClass]
public class InstanceDataTests
{
    private static InstancePaths PathsFor(TestDatabase database) => new(new StorageOptions
    {
        StatePath = database.RootPath,
        MarkdownRootPath = Path.Combine(database.RootPath, "content"),
        KeyRingPath = Path.Combine(database.RootPath, "keys"),
    });

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static async Task<(InstancePaths Paths, SqliteInputEntryRepository Inputs, SqliteReflectionRepository Reflections,
        SqliteDeviceRepository Devices, SqlitePublicationRepository Publications, SqlitePublishTargetRepository Targets,
        SqliteAppSettingStore Settings, SqliteUnitOfWork UnitOfWork, TestClock Clock, TestContentProvider Content)>
        BuildAsync(TestDatabase database, DateTimeOffset? now = null)
    {
        var paths = PathsFor(database);
        paths.EnsureCreated();

        var clock = new TestClock(now ?? new DateTimeOffset(new DateOnly(2026, 3, 11), new TimeOnly(9, 0), TimeSpan.Zero));

        return (
            paths,
            new SqliteInputEntryRepository(database.Accessor),
            new SqliteReflectionRepository(database.Accessor),
            new SqliteDeviceRepository(database.Accessor),
            new SqlitePublicationRepository(database.Accessor),
            new SqlitePublishTargetRepository(database.Accessor),
            new SqliteAppSettingStore(database.Accessor, clock),
            new SqliteUnitOfWork(database.Accessor),
            clock,
            new TestContentProvider(ContentSettings.Default));
    }

    private static BuildInstanceDataUseCase Build(
        SqliteReflectionRepository reflections,
        SqliteInputEntryRepository inputs,
        SqliteTopicRepository topics,
        SqlitePublicationRepository publications,
        SqlitePublishTargetRepository targets,
        SqliteAppSettingStore settings,
        TestContentProvider content,
        SqliteMigrator migrator,
        TestClock clock) =>
        new(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);

    [TestMethod]
    public async Task A_readable_export_contains_the_markdown_the_data_and_the_recordings()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);

        var audio = new FileAudioStore(paths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var writer = new FileInstanceExportWriter(paths, NullLogger<FileInstanceExportWriter>.Instance);
        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var useCase = new CreateExportUseCase(build, writer, audio, clock);

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        var root = Path.Combine(paths.ExportPath, result.RelativeRoot);
        Assert.IsTrue(Directory.Exists(root));

        // §15.1: 每篇随想的 Markdown.
        var markdown = Directory.EnumerateFiles(Path.Combine(root, "markdown"), "*.md").Single();
        var text = await File.ReadAllTextAsync(markdown);
        StringAssert.Contains(text, "title: \"今天的记录\"");
        StringAssert.Contains(text, "draft: true");

        // §15.1: 输入、主题、来源映射和元数据 JSON.
        foreach (var name in new[] { "inputs.json", "topics.json", "reflections.json", "versions.json", "publications.json", "app-settings.json" })
        {
            Assert.IsTrue(File.Exists(Path.Combine(root, "data", name)), $"data/{name} is missing from the export.");
        }

        // §15.1: 尚未被保留策略删除的音频文件.
        Assert.AreEqual(1, result.CopiedBlobs);
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(root, "audio"), "*", SearchOption.AllDirectories).Count());

        // The manifest sits at the root of the package — it describes the whole of it — and states the retention
        // policy in force, which §15.1 requires the export to disclose.
        var manifest = await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"));
        StringAssert.Contains(manifest, "audioRetention");
        StringAssert.Contains(manifest, "30");

        // The source map survived: §15.2 step 5 samples exactly this after a restore.
        var versions = await File.ReadAllTextAsync(Path.Combine(root, "data", "versions.json"));
        StringAssert.Contains(versions, "quoteHash");
        StringAssert.Contains(versions, "sources");
    }

    [TestMethod]
    public async Task No_export_or_backup_carries_a_secret()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        // A credential the operator typed into the admin page — the only way one can exist since A.27. It is
        // stored encrypted outside the backup set, so nothing in these packages may contain its value.
        var uiSecrets = new EncryptedUiSecretStore(paths);
        await uiSecrets.SetAsync("smtp-password", "smtp-do-not-export-me-000000000000", CancellationToken.None);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var exportWriter = new FileInstanceExportWriter(paths, NullLogger<FileInstanceExportWriter>.Instance);

        var export = await new CreateExportUseCase(build, exportWriter, audio, clock).ExecuteAsync(CancellationToken.None);

        var exportRoot = Path.Combine(paths.ExportPath, export.RelativeRoot);

        foreach (var path in Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var text = System.Text.Encoding.UTF8.GetString(bytes);

            Assert.IsFalse(
                text.Contains("smtp-do-not-export-me", StringComparison.Ordinal),
                $"{Path.GetFileName(path)} contains a credential the admin page stored (§10.4).");
        }

        var backupWriter = new ZipBackupWriter(paths, new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance), Configuration(), NullLogger<ZipBackupWriter>.Instance);
        var backup = await new CreateBackupUseCase(build, backupWriter, audio, clock).ExecuteAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(Path.Combine(paths.BackupPath, backup.FileName));

        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

            Assert.IsFalse(
                text.Contains("smtp-do-not-export-me", StringComparison.Ordinal),
                $"{entry.FullName} contains a credential the admin page stored (§10.4).");

            Assert.IsFalse(
                text.Contains("ui-secrets", StringComparison.Ordinal),
                $"{entry.FullName} names the credential store, which means the backup touched it.");
        }

        // §15.2 step 7 asks for exactly this check, so the test asserts the exclusion rather than trusting it.
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Contains("keys", StringComparison.OrdinalIgnoreCase)),
            "The DataProtection key ring must not be in a backup (A.14).");
    }

    /// <summary>
    /// Found by the phase-five acceptance run, which asked for an export and then a backup inside the same second:
    /// the package names carry a one-second stamp, and the second request used to replace the first. For a feature
    /// whose promise is "here is your copy" and "the last seven copies", losing one to a fast double press is the
    /// wrong trade, so the name is made unique instead.
    /// </summary>
    [TestMethod]
    public async Task Two_packages_taken_in_the_same_second_do_not_replace_each_other()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var exports = new CreateExportUseCase(
            build,
            new FileInstanceExportWriter(paths, NullLogger<FileInstanceExportWriter>.Instance),
            audio,
            clock);

        var first = await exports.ExecuteAsync(CancellationToken.None);
        var second = await exports.ExecuteAsync(CancellationToken.None);

        Assert.AreNotEqual(first.RelativeRoot, second.RelativeRoot, "Two exports must be two packages.");
        Assert.IsTrue(
            Directory.Exists(Path.Combine(paths.ExportPath, first.RelativeRoot)),
            "Taking a second export must not delete the first one.");

        var backupWriter = new ZipBackupWriter(
            paths,
            new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance),
            Configuration(),
            NullLogger<ZipBackupWriter>.Instance);

        var backups = new CreateBackupUseCase(
            build,
            backupWriter,
            audio,
            clock);

        var one = await backups.ExecuteAsync(CancellationToken.None);
        var two = await backups.ExecuteAsync(CancellationToken.None);

        Assert.AreNotEqual(one.FileName, two.FileName, "Two backups must be two archives.");
        Assert.IsTrue(File.Exists(Path.Combine(paths.BackupPath, one.FileName)), "The older archive must survive §15.2's rotation.");
        Assert.IsTrue(File.Exists(Path.Combine(paths.BackupPath, two.FileName)));

        // Both archives are listed, so "the last seven copies" counts archives rather than seconds.
        Assert.AreEqual(2, (await backupWriter.ListAsync(CancellationToken.None)).Count);
    }

    /// <summary>
    /// Rotation has to be decided by the order the writer handed the names out, not by the file system's timestamps:
    /// archives written inside the same second report the same time, and a prune that sorted on that would keep an
    /// arbitrary pair — which can mean deleting the newest backup and keeping the oldest.
    /// </summary>
    [TestMethod]
    public async Task Pruning_keeps_the_newest_archives_even_when_they_share_a_timestamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var writer = new ZipBackupWriter(
            paths,
            new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance),
            // No Backup:KeepCount override: the write path keeps §15.2's default seven, and the explicit prune below
            // passes its own count.
            Configuration(),
            NullLogger<ZipBackupWriter>.Instance);

        var backups = new CreateBackupUseCase(build, writer, audio, clock);

        for (var index = 0; index < 4; index++)
        {
            await backups.ExecuteAsync(CancellationToken.None);
        }

        var expected = new[]
        {
            "dailymusings-20260311-090000-3.zip",
            "dailymusings-20260311-090000-4.zip",
        };

        foreach (var name in expected)
        {
            Assert.IsTrue(File.Exists(Path.Combine(paths.BackupPath, name)), $"The run should have written {name}.");
        }

        await writer.PruneAsync(2, CancellationToken.None);

        var remaining = (await writer.ListAsync(CancellationToken.None)).Select(summary => summary.FileName).ToArray();

        CollectionAssert.AreEquivalent(expected, remaining, "§15.2 keeps the newest copies, in the order they were taken.");
    }

    /// <summary>
    /// §15.2 and decision A.13: a backup carries content, never credentials. The check is made by opening the
    /// snapshot inside the archive and looking at the tables, because that is where a device token would be.
    /// </summary>
    [TestMethod]
    public async Task A_backup_strips_device_tokens_and_detaches_what_referenced_them()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, devices, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        var device = DailyMusings.Domain.Identity.Device.Register(
            DeviceId.New(),
            "Pixel 8",
            "hash-of-a-token",
            "android",
            clock.UtcNow);

        await devices.AddAsync(device, CancellationToken.None);
        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true, deviceId: device.Id);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var snapshotter = new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance);
        var writer = new ZipBackupWriter(paths, snapshotter, Configuration(), NullLogger<ZipBackupWriter>.Instance);

        var summary = await new CreateBackupUseCase(build, writer, audio, clock).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, summary.RedactedDeviceTokens, "The snapshot should report how many credentials it removed.");

        using var archive = ZipFile.OpenRead(Path.Combine(paths.BackupPath, summary.FileName));
        var databaseEntry = archive.GetEntry("dailymusings.db");
        Assert.IsNotNull(databaseEntry, "A backup must contain the database snapshot.");

        var extracted = Path.Combine(paths.BackupPath, "inspected.db");
        databaseEntry.ExtractToFile(extracted, overwrite: true);

        // Scoped so the connection is closed before the extracted copy is removed; on Windows an open handle makes
        // the delete fail, which is the same reason the production code disables pooling for these connections.
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = extracted, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();

        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM device;"), "No device row may survive (A.13).");
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pairing_code;"));

        // The foreign key has to be detached, not left dangling: a restored database whose entries point at devices
        // that no longer exist would refuse to open.
        Assert.IsNotNull(await ScalarAsync(connection, "SELECT COUNT(*) FROM input_entry WHERE device_id IS NOT NULL;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM input_entry WHERE device_id IS NOT NULL;"));

            // The content itself is untouched by the redaction.
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM reflection;"));
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM input_entry;"));
        }

        File.Delete(extracted);
    }

    /// <summary>
    /// Deleting a row is not the same as removing its bytes: SQLite leaves the old page content behind, and a plain
    /// <c>grep</c> over the snapshot then still finds a device token's digest. The digest alone cannot authenticate
    /// anybody, but "no device credential in a backup" (A.13) has to hold up when somebody checks the file rather
    /// than the tables — which is what the phase-five restore verification does.
    /// </summary>
    [TestMethod]
    public async Task A_backup_snapshot_does_not_keep_the_bytes_of_a_removed_token()
    {
        const string tokenHash = "5f3a91c7d20b48e6a1c05b7e93d824fb1687ac03e59d7b2468c0f19a3b7d5e82";

        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, devices, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        await devices.AddAsync(
            DailyMusings.Domain.Identity.Device.Register(DeviceId.New(), "Pixel 8", tokenHash, "android", clock.UtcNow),
            CancellationToken.None);
        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var writer = new ZipBackupWriter(
            paths,
            new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance),
            Configuration(),
            NullLogger<ZipBackupWriter>.Instance);

        var summary = await new CreateBackupUseCase(build, writer, audio, clock).ExecuteAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(Path.Combine(paths.BackupPath, summary.FileName));
        var entry = archive.GetEntry("dailymusings.db");
        Assert.IsNotNull(entry);

        var extracted = Path.Combine(paths.BackupPath, "scanned.db");
        entry.ExtractToFile(extracted, overwrite: true);

        var bytes = await File.ReadAllBytesAsync(extracted);
        var text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.IsFalse(
            text.Contains(tokenHash, StringComparison.Ordinal),
            "The snapshot still carries the digest of a credential it claims to have removed (A.13).");

        File.Delete(extracted);
    }

    [TestMethod]
    public async Task A_backup_keeps_the_newest_archives_only()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        paths.EnsureCreated();

        var writer = new ZipBackupWriter(
            paths,
            new SqliteDatabaseSnapshotter(database.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance),
            Configuration(("Backup:KeepCount", "3")),
            NullLogger<ZipBackupWriter>.Instance);

        for (var index = 0; index < 5; index++)
        {
            var stamp = new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero).AddDays(index);
            await writer.WriteAsync(new BackupContent([], [], "{}", "30"), stamp, CancellationToken.None);
        }

        Assert.AreEqual(5, (await writer.ListAsync(CancellationToken.None)).Count);

        var removed = await writer.PruneAsync(keep: 7, CancellationToken.None);

        Assert.AreEqual(2, removed, "The configured count wins over the argument, and the newest are kept.");
        Assert.AreEqual(3, (await writer.ListAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task A_staged_restore_is_validated_before_anything_is_applied()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        paths.EnsureCreated();

        var stager = new StagedRestoreService(paths, NullLogger<StagedRestoreService>.Instance);

        // Something that is not a backup at all.
        using (var junk = new MemoryStream("this is not a zip file"u8.ToArray()))
        {
            var rejected = await stager.StageAsync(junk, CancellationToken.None);

            Assert.IsFalse(rejected.IsValid);
            Assert.AreEqual("restore.archive.unreadable", rejected.Code);
        }

        // A zip, but not one of ours.
        using (var wrong = new MemoryStream())
        {
            using (var archive = new ZipArchive(wrong, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
                await writer.WriteAsync("hello");
            }

            wrong.Position = 0;
            var rejected = await stager.StageAsync(wrong, CancellationToken.None);

            Assert.IsFalse(rejected.IsValid);
            Assert.AreEqual("restore.archive.not_a_backup", rejected.Code);
        }

        Assert.IsNull(await stager.GetPendingAsync(CancellationToken.None), "A refused archive stages nothing.");
    }

    [TestMethod]
    public async Task A_restore_refuses_an_archive_that_still_carries_device_tokens()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, devices, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(database);
        var topics = new SqliteTopicRepository(database.Accessor);
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(paths);

        await devices.AddAsync(
            DailyMusings.Domain.Identity.Device.Register(DeviceId.New(), "Pixel 8", "hash", "android", clock.UtcNow),
            CancellationToken.None);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        // An archive built from the live database without sanitization — exactly what a hand-made or tampered
        // "backup" would look like.
        var raw = Path.Combine(paths.BackupPath, "raw.db");
        Directory.CreateDirectory(paths.BackupPath);
        await using (var connection = await database.Accessor.GetConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"VACUUM INTO '{raw}';";
            await command.ExecuteNonQueryAsync();
        }

        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(raw, "dailymusings.db");
        }

        archiveStream.Position = 0;

        var stager = new StagedRestoreService(paths, NullLogger<StagedRestoreService>.Instance);
        var validation = await stager.StageAsync(archiveStream, CancellationToken.None);

        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual("restore.archive.contains_device_tokens", validation.Code);
        Assert.IsNull(await stager.GetPendingAsync(CancellationToken.None));
    }

    /// <summary>
    /// §15.2 step 3 taken literally: a staged restore is applied at the next start, before the instance serves
    /// anything. The test drives the same entry point the host does.
    /// </summary>
    [TestMethod]
    public async Task A_staged_restore_replaces_the_database_and_the_recordings_at_startup()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourcePaths = PathsFor(source);
        sourcePaths.EnsureCreated();

        var (_, inputs, reflections, _, publications, targets, settings, unitOfWork, clock, content) = await BuildAsync(source);
        var topics = new SqliteTopicRepository(source.Accessor);
        var migrator = new SqliteMigrator(source.Accessor, NullLogger<SqliteMigrator>.Instance);
        var audio = new FileAudioStore(sourcePaths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true);

        var build = Build(reflections, inputs, topics, publications, targets, settings, content, migrator, clock);
        var summary = await new CreateBackupUseCase(
            build,
            new ZipBackupWriter(sourcePaths, new SqliteDatabaseSnapshotter(source.Accessor, NullLogger<SqliteDatabaseSnapshotter>.Instance), Configuration(), NullLogger<ZipBackupWriter>.Instance),
            audio,
            clock).ExecuteAsync(CancellationToken.None);

        var archivePath = Path.Combine(sourcePaths.BackupPath, summary.FileName);

        // A second, empty instance — "a fresh directory with only the compose file and new secrets" (§15.2 step 1).
        // 它的状态根刻意是 target 目录下的子目录：恢复是在**下次启动、库还没被打开**时执行的，而 TestDatabase
        // 已经握着一个连接；把两者指到同一个文件，测出来的只会是「Windows 不许删别人开着的 -wal」。
        await using var target = await TestDatabase.CreateAsync();
        var targetPaths = new InstancePaths(new StorageOptions
        {
            StatePath = Path.Combine(target.RootPath, "state"),
            MarkdownRootPath = Path.Combine(target.RootPath, "content"),
        });
        targetPaths.EnsureCreated();

        var configuration = Configuration(
            ("Storage:StatePath", targetPaths.RootPath),
            ("Storage:MarkdownRootPath", targetPaths.MarkdownPath),
            ("Storage:KeyRingPath", targetPaths.KeyRingPath));

        var stager = new StagedRestoreService(targetPaths, NullLogger<StagedRestoreService>.Instance);

        await using (var stream = File.OpenRead(archivePath))
        {
            var validation = await stager.StageAsync(stream, CancellationToken.None);
            Assert.IsTrue(validation.IsValid, validation.Detail);
        }

        // 恢复之前，这个空实例连库都还没有——§15.2 第 1 步说的就是「一个全新目录」。
        Assert.IsFalse(File.Exists(targetPaths.DatabasePath), "A fresh instance starts with no database.");

        var applied = await StagedRestoreStartupTask.ApplyAsync(
            configuration,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.IsNotNull(applied, "The startup task should report what it applied.");

        // Read through a fresh connection, which is what a restarted process has: the accessor the test opened
        // earlier still points at the file it first opened, exactly as a long-lived process would.
        await using var restoredAccessor = new SqliteConnectionAccessor(targetPaths.DatabasePath);
        var restored = new SqliteReflectionRepository(restoredAccessor);
        var reflectionsAfter = await restored.ListAllAsync(10, CancellationToken.None);

        Assert.AreEqual(1, reflectionsAfter.Count, "The restored instance has the archived day.");
        Assert.AreEqual(ReflectionStatus.Confirmed, reflectionsAfter[0].Status);
        Assert.IsNotNull(reflectionsAfter[0].ConfirmedAtUtc, "The confirmation time travels with the backup (A.1).");

        var restoredInput = (await new SqliteInputEntryRepository(restoredAccessor).ListAllAsync(10, CancellationToken.None)).Single();
        Assert.AreEqual("今天试着记录了一点东西。", restoredInput.TranscriptForGeneration);
        Assert.IsNull(restoredInput.DeviceId, "A restore brings back content, not credentials (A.13).");

        // The recording came across, and it is where the database says it is.
        Assert.IsTrue(restoredInput.HasAudio);
        var restoredBlob = targetPaths.ResolveMediaFile(restoredInput.AudioPath!);
        Assert.IsTrue(File.Exists(restoredBlob), $"Expected the recording at {restoredBlob}.");

        // Applying it consumed the staging area, so a second start does not restore twice.
        Assert.IsNull(await stager.GetPendingAsync(CancellationToken.None));
        Assert.IsNull(await StagedRestoreStartupTask.ApplyAsync(configuration, NullLogger.Instance, CancellationToken.None));
    }

    [TestMethod]
    public async Task The_retention_sweep_deletes_recordings_whose_window_has_passed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, _, _, _, unitOfWork, clock, content) = await BuildAsync(database);
        var audio = new FileAudioStore(paths);

        // Confirmed 40 days ago, with a 30-day policy, so the window has passed.
        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true, confirmedAtUtc: clock.UtcNow.AddDays(-40));

        var settings = new SqliteAppSettingStore(database.Accessor, clock);
        await settings.SetAsync(ContentSettings.AudioRetentionKey, "30", CancellationToken.None);

        var cleanup = new RunAudioCleanupUseCase(reflections, inputs, audio, content, clock);
        var result = await cleanup.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, result.DeletedBlobs);
        Assert.AreEqual(0, result.FailedDeletions);
        Assert.IsTrue(result.ReleasedBytes > 0);

        var entry = (await inputs.ListAllAsync(10, CancellationToken.None)).Single();
        Assert.IsFalse(entry.HasAudio, "The path is cleared once the blob is gone (§15.1).");
        Assert.IsNotNull(entry.AudioDeletedAtUtc);
        Assert.IsNotNull(entry.TranscriptForGeneration, "The transcript stays: only the recording is deleted.");
        Assert.AreEqual("audio/mp4", entry.AudioContentType);
    }

    [TestMethod]
    public async Task The_retention_sweep_leaves_a_recent_confirmation_alone()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, _, _, _, unitOfWork, clock, content) = await BuildAsync(database);
        var audio = new FileAudioStore(paths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true, confirmedAtUtc: clock.UtcNow.AddDays(-5));

        var result = await new RunAudioCleanupUseCase(reflections, inputs, audio, content, clock)
            .ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, result.DeletedBlobs);
        Assert.IsTrue((await inputs.ListAllAsync(10, CancellationToken.None)).Single().HasAudio);
    }

    [TestMethod]
    public async Task Keep_forever_means_the_sweep_does_nothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (paths, inputs, reflections, _, _, _, _, unitOfWork, clock, _) = await BuildAsync(database);
        var audio = new FileAudioStore(paths);

        await SeedAsync(inputs, reflections, unitOfWork, clock, audio, confirmed: true, confirmedAtUtc: clock.UtcNow.AddDays(-9999));

        var settings = new SqliteAppSettingStore(database.Accessor, clock);
        await settings.SetAsync(ContentSettings.AudioRetentionKey, AudioRetentionPolicy.KeepForever.ToString(), CancellationToken.None);

        var result = await new RunAudioCleanupUseCase(
                reflections,
                inputs,
                audio,
                new TestContentProvider(ContentSettings.Default with { AudioRetentionDays = AudioRetentionPolicy.KeepForever }),
                clock)
            .ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, result.DeletedBlobs);
        Assert.AreEqual(0, result.CandidateDays);
        Assert.IsTrue((await inputs.ListAllAsync(10, CancellationToken.None)).Single().HasAudio);
    }

    private static async Task SeedAsync(
        SqliteInputEntryRepository inputs,
        SqliteReflectionRepository reflections,
        SqliteUnitOfWork unitOfWork,
        TestClock clock,
        IAudioStore audio,
        bool confirmed,
        DateTimeOffset? confirmedAtUtc = null,
        DeviceId? deviceId = null)
    {
        var contentDate = ContentDate.From(new DateOnly(2026, 3, 11));
        var inputId = InputEntryId.New();

        var entry = InputEntry.CreateVoice(
            inputId,
            clock.UtcNow.AddHours(-2),
            480,
            contentDate,
            "pending",
            TimeSpan.FromSeconds(9),
            "audio/mp4",
            clientIdempotencyKey: null,
            deviceId: deviceId);

        await using (var stream = new MemoryStream("fake-m4a-bytes"u8.ToArray()))
        {
            var stored = await audio.SaveAsync(inputId, contentDate, stream, "audio/mp4", CancellationToken.None);
            entry = InputEntry.Rehydrate(
                entry.Id,
                entry.SourceType,
                entry.CreatedAtUtc,
                entry.CreatedOffsetMinutes,
                entry.ContentDate,
                stored.RelativePath,
                stored.ContentType,
                TimeSpan.FromSeconds(9),
                null,
                null,
                null,
                TranscriptionStatus.InProgress,
                null,
                true,
                null,
                null,
                null,
                null,
                deviceId);
        }

        entry.CompleteTranscription("今天试着记录了一点东西。");

        var reflection = Reflection.Create(ReflectionId.New(), contentDate, GenerationReason.Scheduled, clock.UtcNow);
        reflection.MarkReady(clock.UtcNow);
        reflection.BeginGeneration(GenerationReason.Scheduled, clock.UtcNow);

        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflection.Id,
            "今天的记录",
            "摘要",
            "第一段。",
            WritingSettings.Default,
            new ModelInfo("test-writer"),
            "generation-v1",
            clock.UtcNow,
            tags: ["记录"],
            categories: ["随想"]);

        reflection.ApplyGeneratedVersion(version.Id, false, false, clock.UtcNow);

        if (confirmed)
        {
            reflection.Confirm(version.Id, confirmedAtUtc ?? clock.UtcNow);
        }

        await using var transaction = await unitOfWork.BeginAsync(CancellationToken.None);
        await inputs.AddAsync(entry, CancellationToken.None);
        await reflections.AddAsync(reflection, CancellationToken.None);
        await reflections.AddVersionAsync(version, CancellationToken.None);
        await reflections.ReplaceSourcesAsync(
            version.Id,
            [
                DailyMusings.Domain.Reflections.Sources.SourceReference.Create(
                    SourceReferenceId.New(),
                    version.Id,
                    0,
                    0,
                    3,
                    "第一段",
                    entry.Id,
                    0.9,
                    "与当天记录一致",
                    isHistorical: false),
            ],
            CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
