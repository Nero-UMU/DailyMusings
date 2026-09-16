using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Notifications;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Application.Operations;

/// <summary>
/// Composes the manifest every package carries (docs/开发指导.md §15.1, §15.2).
/// <para>
/// A package whose contents nobody can enumerate is not a backup, it is a hope. The manifest lists what is inside,
/// what was deliberately left out and why, and the retention policy the instance was running under at the time —
/// which §15.1 requires the export to state, because "your recordings are kept for thirty days" is a promise that
/// only means something if the package records whether it was in force.
/// </para>
/// </summary>
public static class InstanceDataManifest
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Compose(
        string kind,
        DateTimeOffset nowUtc,
        ContentSettings settings,
        string? schemaVersion,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string> blobs)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["createdAtUtc"] = nowUtc.ToString("o", CultureInfo.InvariantCulture),
            ["contentTimeZone"] = settings.TimeZoneId,
            ["generationLocalTime"] = ContentSettings.FormatTime(settings.GenerationLocalTime),
            ["publishLocalTime"] = ContentSettings.FormatTime(settings.PublishLocalTime),
            ["audioRetention"] = new AudioRetentionPolicy(settings.AudioRetentionDays).Describe(),
            ["schemaVersion"] = schemaVersion,
            ["counts"] = counts,
            ["audioFiles"] = blobs,

            // §15.2 and §10.4: the two things a package must never carry, named explicitly so that an operator can
            // check the claim rather than take it on trust.
            ["excluded"] = new[]
            {
                "设备令牌及其派生的会话状态（decision A.13）：恢复后设备必须重新配对。",
                "DataProtection 密钥环（decision A.14）：它签发管理员登录 Cookie，属凭据等价物，不进备份。",
                "任何 Secret（§10.4）：模型密钥、SMTP 密码与 WordPress Application Password 只按名引用。",
            },
        };

        return JsonSerializer.Serialize(manifest, Options);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>One input as the export writes it. Field names are stable: an export is read by other tools.</summary>
public sealed record ExportedInput(
    string Id,
    string SourceType,
    string ContentDate,
    string CreatedAtUtc,
    int CreatedOffsetMinutes,
    string TranscriptionStatus,
    string? OriginalTranscript,
    string? RevisedTranscript,
    string? TranscriptionErrorCode,
    bool AllowFutureRecall,
    string? PrimaryTopicId,
    IReadOnlyList<string> SecondaryTopicIds,
    string? AudioPath,
    string? AudioContentType,
    double? AudioDurationSeconds,
    string? AudioDeletedAtUtc,
    string? DeletedAtUtc,
    string? DeviceId);

public sealed record ExportedTopic(string Id, string Name, string CreatedAtUtc, string? MergedIntoId, string? MergedAtUtc);

public sealed record ExportedSource(
    string InputId,
    int BlockIndex,
    int CharStart,
    int CharEnd,
    string QuoteHash,
    double Relevance,
    string Reason,
    bool IsHistorical);

public sealed record ExportedUnsourcedClaim(int BlockIndex, int CharStart, int CharEnd, string Reason);

public sealed record ExportedVersion(
    string Id,
    string ReflectionId,
    string Slot,
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    bool HasManualEdits,
    string CreatedAtUtc,
    string? EditedAtUtc,
    string? ModelName,
    string? PromptVersion,
    string? SourcesCheckedAtUtc,
    IReadOnlyList<ExportedSource> Sources,
    IReadOnlyList<ExportedUnsourcedClaim> UnsourcedClaims);

public sealed record ExportedReflection(
    string Id,
    string ContentDate,
    string Status,
    string GenerationReason,
    string? LastStaleReason,
    string? InitialVersionId,
    string? PreviousVersionId,
    string? WorkingVersionId,
    string? ConfirmedVersionId,
    string? ConfirmedAtUtc,
    string CreatedAtUtc,
    string UpdatedAtUtc);

public sealed record ExportedPublication(
    string Id,
    string ReflectionId,
    string ReflectionVersionId,
    string PublishTargetName,
    string TargetType,
    string Trigger,
    string Status,
    string? RemoteId,
    string? ScheduledAtUtc,
    string? CompletedAtUtc,
    string? ErrorCode);

/// <summary>Reads the instance and turns it into human-readable files (docs/开发指导.md §15.1).</summary>
public sealed class BuildInstanceDataUseCase
{
    private const int MaxRows = 100_000;

    private readonly IReflectionRepository _reflections;
    private readonly IInputEntryRepository _inputs;
    private readonly ITopicRepository _topics;
    private readonly IPublicationRepository _publications;
    private readonly IPublishTargetRepository _targets;
    private readonly IAppSettingStore _settings;
    private readonly IContentSettingsProvider _content;
    private readonly IMigrationRunner _migrations;
    private readonly IClock _clock;

    public BuildInstanceDataUseCase(
        IReflectionRepository reflections,
        IInputEntryRepository inputs,
        ITopicRepository topics,
        IPublicationRepository publications,
        IPublishTargetRepository targets,
        IAppSettingStore settings,
        IContentSettingsProvider content,
        IMigrationRunner migrations,
        IClock clock)
    {
        _reflections = reflections;
        _inputs = inputs;
        _topics = topics;
        _publications = publications;
        _targets = targets;
        _settings = settings;
        _content = content;
        _migrations = migrations;
        _clock = clock;
    }

    /// <summary>The files and blobs a package should contain, plus the counts its manifest reports.</summary>
    public async Task<(IReadOnlyList<ExportedFile> Files, IReadOnlyList<ExportedBlob> Blobs, IReadOnlyDictionary<string, int> Counts, ContentSettings Settings, string? SchemaVersion)>
        ExecuteAsync(bool includeAppSettings, string blobRoot, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var settings = await _content.GetAsync(cancellationToken).ConfigureAwait(false);

        var reflections = await _reflections.ListAllAsync(MaxRows, cancellationToken).ConfigureAwait(false);
        var inputs = await _inputs.ListAllAsync(MaxRows, cancellationToken).ConfigureAwait(false);
        var topics = await _topics.ListAsync(includeMerged: true, cancellationToken).ConfigureAwait(false);
        var targets = await _targets.ListAsync(cancellationToken).ConfigureAwait(false);
        var targetLookup = targets.ToDictionary(target => target.Id);

        var files = new List<ExportedFile>();
        var blobs = new List<ExportedBlob>();
        var versions = new List<ExportedVersion>();
        var publications = new List<ExportedPublication>();

        foreach (var reflection in reflections.OrderBy(item => item.ContentDate))
        {
            // Every slot that still exists, with its source map and findings. §15.2's restore checks sample exactly
            // this: that the three version positions survived and that a source mapping still resolves.
            foreach (var (slot, versionId) in new (string, ReflectionVersionId?)[]
                     {
                         ("initial", reflection.InitialVersionId),
                         ("previous", reflection.PreviousVersionId),
                         ("working", reflection.WorkingVersionId),
                         ("confirmed", reflection.ConfirmedVersionId),
                     })
            {
                if (versionId is not { } id)
                {
                    continue;
                }

                var version = await _reflections.FindVersionAsync(id, cancellationToken).ConfigureAwait(false);
                if (version is null)
                {
                    continue;
                }

                versions.Add(new ExportedVersion(
                    version.Id.ToString(),
                    version.ReflectionId.ToString(),
                    slot,
                    version.Title,
                    version.Summary,
                    version.Body,
                    version.Tags,
                    version.Categories,
                    version.HasManualEdits,
                    version.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    version.EditedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
                    version.ModelInfo?.ModelName,
                    version.PromptVersion,
                    version.SourcesCheckedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
                    version.Sources
                        .Select(source => new ExportedSource(
                            source.InputId.ToString(),
                            source.BlockIndex,
                            source.CharStart,
                            source.CharEnd,
                            source.QuoteHash,
                            source.Relevance,
                            source.Reason,
                            source.IsHistorical))
                        .ToArray(),
                    version.UnsourcedClaims
                        .Select(claim => new ExportedUnsourcedClaim(claim.BlockIndex, claim.CharStart, claim.CharEnd, claim.Reason))
                        .ToArray()));

                if (slot == "working")
                {
                    // The article itself, rendered the same way the Markdown target renders it, so the export is
                    // readable on its own rather than only through a viewer this project wrote.
                    var document = MarkdownDocument.From(version, reflection.ContentDate);
                    files.Add(new ExportedFile(
                        $"markdown/{MarkdownFileName.BaseName(reflection.ContentDate, document.Slug)}.md",
                        MarkdownTemplate.DefaultTemplate.Render(document)));
                }
            }

            foreach (var publication in await _publications
                         .ListByReflectionAsync(reflection.Id, cancellationToken)
                         .ConfigureAwait(false))
            {
                var target = targetLookup.GetValueOrDefault(publication.PublishTargetId);

                publications.Add(new ExportedPublication(
                    publication.Id.ToString(),
                    publication.ReflectionId.ToString(),
                    publication.ReflectionVersionId.ToString(),
                    target?.Name ?? publication.PublishTargetId.ToString(),
                    target?.Type == PublishTargetType.Markdown ? "markdown" : "wordPress",
                    publication.Trigger == PublicationTrigger.Manual ? "manual" : "automatic",
                    publication.Status.ToString(),
                    publication.RemoteId,
                    publication.ScheduledAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    publication.CompletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
                    publication.ErrorCode));
            }
        }

        foreach (var input in inputs)
        {
            if (input.AudioPath is { Length: > 0 } path)
            {
                blobs.Add(new ExportedBlob(path, $"{blobRoot}/{path}"));
            }
        }

        files.Add(new ExportedFile("data/inputs.json", InstanceDataManifest.Serialize(inputs.Select(input => new ExportedInput(
            input.Id.ToString(),
            input.SourceType == InputSourceType.Voice ? "voice" : "text",
            input.ContentDate.ToString(),
            input.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            input.CreatedOffsetMinutes,
            input.TranscriptionStatus.ToString(),
            input.OriginalTranscript,
            input.RevisedTranscript,
            input.TranscriptionErrorCode,
            input.AllowFutureRecall,
            input.PrimaryTopicId?.ToString(),
            input.SecondaryTopicIds.Select(topicId => topicId.ToString()).ToArray(),
            input.AudioPath,
            input.AudioContentType,
            input.AudioDuration?.TotalSeconds,
            input.AudioDeletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            input.DeletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            input.DeviceId?.ToString())).ToArray())));

        files.Add(new ExportedFile("data/topics.json", InstanceDataManifest.Serialize(topics.Select(topic => new ExportedTopic(
            topic.Id.ToString(),
            topic.Name,
            topic.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            topic.MergedIntoId?.ToString(),
            topic.MergedAtUtc?.ToString("o", CultureInfo.InvariantCulture))).ToArray())));

        files.Add(new ExportedFile("data/reflections.json", InstanceDataManifest.Serialize(reflections.Select(reflection => new ExportedReflection(
            reflection.Id.ToString(),
            reflection.ContentDate.ToString(),
            reflection.Status.ToString(),
            reflection.GenerationReason.ToString(),
            reflection.LastStaleReason?.ToString(),
            reflection.InitialVersionId?.ToString(),
            reflection.PreviousVersionId?.ToString(),
            reflection.WorkingVersionId?.ToString(),
            reflection.ConfirmedVersionId?.ToString(),
            reflection.ConfirmedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            reflection.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            reflection.UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture))).ToArray())));

        files.Add(new ExportedFile("data/versions.json", InstanceDataManifest.Serialize(versions)));
        files.Add(new ExportedFile("data/publications.json", InstanceDataManifest.Serialize(publications)));

        if (includeAppSettings)
        {
            // Non-secret instance configuration only. The secret store is a different thing entirely and is never
            // read here (§10.4) — which is what lets §15.2 step 7 be checkable rather than aspirational.
            var values = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);
            files.Add(new ExportedFile("data/app-settings.json", InstanceDataManifest.Serialize(values)));
        }

        var applied = await _migrations.GetAppliedAsync(cancellationToken).ConfigureAwait(false);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["reflections"] = reflections.Count,
            ["inputs"] = inputs.Count,
            ["topics"] = topics.Count,
            ["versions"] = versions.Count,
            ["sourceReferences"] = versions.Sum(version => version.Sources.Count),
            ["unsourcedClaims"] = versions.Sum(version => version.UnsourcedClaims.Count),
            ["publications"] = publications.Count,
            ["audioFiles"] = blobs.Count,
            ["exportedFiles"] = files.Count,
        };

        return (files, blobs, counts, settings, applied.LastOrDefault());
    }

    /// <summary>The blob list with the ones that have gone missing removed, so the manifest never promises a file
    /// the package does not contain.</summary>
    public async Task<(IReadOnlyList<ExportedBlob> Blobs, IReadOnlyList<string> MissingPaths)> VerifyBlobsAsync(
        IReadOnlyList<ExportedBlob> blobs,
        IAudioStore audio,
        CancellationToken cancellationToken)
    {
        var present = new List<ExportedBlob>();
        var missing = new List<string>();

        foreach (var blob in blobs)
        {
            if (await audio.ExistsAsync(blob.StoredPath, cancellationToken).ConfigureAwait(false))
            {
                present.Add(blob);
            }
            else
            {
                missing.Add(blob.StoredPath);
            }
        }

        return (present, missing);
    }
}

/// <summary>Writes a readable export (docs/开发指导.md §15.1, §13).</summary>
public sealed class CreateExportUseCase
{
    private readonly BuildInstanceDataUseCase _build;
    private readonly IInstanceExportWriter _writer;
    private readonly IAudioStore _audio;
    private readonly IClock _clock;

    public CreateExportUseCase(
        BuildInstanceDataUseCase build,
        IInstanceExportWriter writer,
        IAudioStore audio,
        IClock clock)
    {
        _build = build;
        _writer = writer;
        _audio = audio;
        _clock = clock;
    }

    public async Task<InstanceExportResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var (files, blobs, counts, settings, schema) = await _build
            .ExecuteAsync(includeAppSettings: true, blobRoot: "audio", cancellationToken)
            .ConfigureAwait(false);

        var (present, missing) = await _build
            .VerifyBlobsAsync(blobs, _audio, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var manifest = InstanceDataManifest.Compose(
            "export",
            now,
            settings,
            schema,
            counts,
            present.Select(blob => blob.RelativePath).ToArray());

        var result = await _writer
            .WriteAsync(
                new InstanceExportRequest(
                    stamp,
                    [.. files, new ExportedFile("manifest.json", manifest)],
                    present,
                    missing),
                cancellationToken)
            .ConfigureAwait(false);

        return result;
    }
}

/// <summary>Takes a complete backup and keeps the last few (docs/开发指导.md §15.2).</summary>
public sealed class CreateBackupUseCase
{
    /// <summary>§15.2: 默认保留最近七份.</summary>
    public const int DefaultRetention = 7;

    private readonly BuildInstanceDataUseCase _build;
    private readonly IBackupWriter _writer;
    private readonly IAudioStore _audio;
    private readonly IClock _clock;

    public CreateBackupUseCase(
        BuildInstanceDataUseCase build,
        IBackupWriter writer,
        IAudioStore audio,
        IClock clock)
    {
        _build = build;
        _writer = writer;
        _audio = audio;
        _clock = clock;
    }

    public async Task<BackupSummary> ExecuteAsync(CancellationToken cancellationToken)
    {
        // The recordings land under `media/` here, not `audio/`: a backup is meant to be restored, and the database
        // in the same archive names its blobs relative to the media root. The readable export can file them anywhere,
        // because nothing reads it back.
        var (files, blobs, counts, settings, schema) = await _build
            .ExecuteAsync(includeAppSettings: true, blobRoot: "media", cancellationToken)
            .ConfigureAwait(false);

        var (present, _) = await _build
            .VerifyBlobsAsync(blobs, _audio, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;

        var manifest = InstanceDataManifest.Compose(
            "backup",
            now,
            settings,
            schema,
            counts,
            present.Select(blob => blob.RelativePath).ToArray());

        var summary = await _writer
            .WriteAsync(new BackupContent(files, present, manifest, new AudioRetentionPolicy(settings.AudioRetentionDays).Describe()), now, cancellationToken)
            .ConfigureAwait(false);

        // Pruning after the new archive exists: if it fails, the operator has one archive too many rather than none.
        await _writer.PruneAsync(DefaultRetention, cancellationToken).ConfigureAwait(false);

        return summary;
    }
}

/// <summary>Reads the packages already on disk, for the admin page.</summary>
public sealed class ListInstanceDataUseCase
{
    private readonly IInstanceExportWriter _exports;
    private readonly IBackupWriter _backups;
    private readonly IRestoreStager _restore;

    public ListInstanceDataUseCase(IInstanceExportWriter exports, IBackupWriter backups, IRestoreStager restore)
    {
        _exports = exports;
        _backups = backups;
        _restore = restore;
    }

    public Task<IReadOnlyList<InstanceExportSummary>> ListExportsAsync(CancellationToken cancellationToken) =>
        _exports.ListAsync(cancellationToken);

    public Task<IReadOnlyList<BackupSummary>> ListBackupsAsync(CancellationToken cancellationToken) =>
        _backups.ListAsync(cancellationToken);

    public Task<RestoreValidation?> GetPendingRestoreAsync(CancellationToken cancellationToken) =>
        _restore.GetPendingAsync(cancellationToken);
}

/// <summary>Validates an uploaded backup and stages it (docs/开发指导.md §15.2 step 3).</summary>
public sealed class StageRestoreUseCase
{
    private readonly IRestoreStager _stager;

    public StageRestoreUseCase(IRestoreStager stager) => _stager = stager;

    public Task<RestoreValidation> ExecuteAsync(Stream archive, CancellationToken cancellationToken) =>
        _stager.StageAsync(archive, cancellationToken);
}

public sealed record AudioCleanupResult(int CandidateDays, int DeletedBlobs, int FailedDeletions, long ReleasedBytes);

/// <summary>
/// The retention sweep for recordings (docs/开发指导.md §15.1, decision A.1).
/// <para>
/// Runs as its own persisted job, and §15.1 is explicit that its failure must not affect anything else: the worst
/// outcome of a sweep that cannot delete a file is that the file is still there next time.
/// </para>
/// </summary>
public sealed class RunAudioCleanupUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;

    public RunAudioCleanupUseCase(
        IReflectionRepository reflections,
        IInputEntryRepository inputs,
        IAudioStore audio,
        IContentSettingsProvider settings,
        IClock clock)
    {
        _reflections = reflections;
        _inputs = inputs;
        _audio = audio;
        _settings = settings;
        _clock = clock;
    }

    public async Task<AudioCleanupResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var policy = new AudioRetentionPolicy(settings.AudioRetentionDays);
        policy.Validate();

        if (policy.KeepsForever)
        {
            // A.1 allows "永久": the sweep does nothing at all rather than deciding on its own what forever means.
            return new AudioCleanupResult(0, 0, 0, 0);
        }

        var now = _clock.UtcNow;

        // The cutoff is the earliest confirmation that could already be due, so the query can use the index instead
        // of loading every day ever confirmed.
        var cutoff = policy.DeletesImmediately ? now : now.AddDays(-policy.Days);
        var days = await _reflections
            .ListConfirmedBeforeAsync(cutoff, 500, cancellationToken)
            .ConfigureAwait(false);

        var deleted = 0;
        var failed = 0;
        long released = 0;
        var candidateDays = 0;

        foreach (var reflection in days)
        {
            var entries = await _inputs
                .ListByContentDateAsync(reflection.ContentDate, cancellationToken)
                .ConfigureAwait(false);

            var cleanable = entries
                .Where(entry => AudioCleanupPolicy.IsCleanable(entry, reflection.ConfirmedAtUtc, policy, now))
                .ToArray();

            if (cleanable.Length == 0)
            {
                continue;
            }

            candidateDays++;

            foreach (var entry in cleanable)
            {
                var path = entry.AudioPath;
                if (path is null)
                {
                    continue;
                }

                var size = await _audio.GetSizeAsync(path, cancellationToken).ConfigureAwait(false);

                try
                {
                    // Blob first, then the row. Same order as the manual delete, and for the same reason: a dangling
                    // path is recoverable, whereas a blob with no row referencing it is invisible forever.
                    await _audio.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Left for the next sweep: the entry still names its blob, so nothing is lost track of.
                    failed++;
                    continue;
                }

                entry.DeleteAudio(now);
                await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

                deleted++;
                released += size ?? 0;
            }
        }

        return new AudioCleanupResult(candidateDays, deleted, failed, released);
    }
}
