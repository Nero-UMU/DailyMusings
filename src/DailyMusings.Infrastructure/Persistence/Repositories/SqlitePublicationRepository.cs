using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Named destinations (docs/开发指导.md §6.7).
/// <para>
/// The row holds a configuration key or a directory, never a credential: the WordPress password lives in the
/// secret store and is referenced by name (§10.4), which is what lets a backup of this table travel safely.
/// </para>
/// </summary>
public sealed class SqlitePublishTargetRepository : IPublishTargetRepository
{
    private const string Columns = """
        id, name, target_type, destination_reference, automatic_publish_enabled,
        automatic_publish_enabled_by, automatic_publish_enabled_at_utc
        """;

    private readonly SqliteConnectionAccessor _accessor;

    public SqlitePublishTargetRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<IReadOnlyList<PublishTarget>> ListAsync(CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {Columns} FROM publish_target ORDER BY name;",
            Map,
            cancellationToken).ConfigureAwait(false);

    public async Task<PublishTarget?> FindByIdAsync(PublishTargetId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM publish_target WHERE id = $id;",
            Map,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    /// <summary>
    /// Name lookup is case-insensitive, because two targets called "Blog" and "blog" would be a mistake nobody
    /// notices until they publish to the wrong one.
    /// </summary>
    public async Task<PublishTarget?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM publish_target WHERE name = $name COLLATE NOCASE;",
            Map,
            cancellationToken,
            ("$name", name.Trim())).ConfigureAwait(false);
    }

    public async Task AddAsync(PublishTarget target, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO publish_target ({Columns})
             VALUES ($id, $name, $type, $destination, $automatic, $enabledBy, $enabledAt);
             """,
            cancellationToken,
            ("$id", target.Id.ToString()),
            ("$name", target.Name),
            ("$type", (int)target.Type),
            ("$destination", SqliteValues.TextOrNull(target.DestinationReference)),
            ("$automatic", target.AutomaticPublishEnabled ? 1 : 0),
            ("$enabledBy", SqliteValues.TextOrNull(target.AutomaticPublishEnabledBy)),
            ("$enabledAt", SqliteValues.InstantOrNull(target.AutomaticPublishEnabledAtUtc))).ConfigureAwait(false);

    public async Task UpdateAsync(PublishTarget target, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE publish_target
               SET name = $name,
                   destination_reference = $destination,
                   automatic_publish_enabled = $automatic,
                   automatic_publish_enabled_by = $enabledBy,
                   automatic_publish_enabled_at_utc = $enabledAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", target.Id.ToString()),
            ("$name", target.Name),
            ("$destination", SqliteValues.TextOrNull(target.DestinationReference)),
            ("$automatic", target.AutomaticPublishEnabled ? 1 : 0),
            ("$enabledBy", SqliteValues.TextOrNull(target.AutomaticPublishEnabledBy)),
            ("$enabledAt", SqliteValues.InstantOrNull(target.AutomaticPublishEnabledAtUtc))).ConfigureAwait(false);

    private static PublishTarget Map(SqliteDataReader reader) =>
        PublishTarget.Rehydrate(
            new PublishTargetId(SqliteIds.Parse(reader.GetString(0))),
            reader.GetString(1),
            (PublishTargetType)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            SqliteValues.ReadBool(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            SqliteValues.ReadInstant(reader, 6));
}

/// <summary>
/// Publication records (docs/开发指导.md §6.7, §11.1).
/// <para>
/// The table's <c>UNIQUE (reflection_version_id, publish_target_id)</c> is the load-bearing part: §14 requires the
/// same version not to publish twice to one target, and enforcing it in the schema means a client retrying a
/// timed-out request cannot produce a second article no matter what the application does.
/// </para>
/// </summary>
public sealed class SqlitePublicationRepository : IPublicationRepository
{
    private const string Columns = """
        id, reflection_id, reflection_version_id, publish_target_id, trigger_kind, status, remote_id,
        attempt_count, scheduled_at_utc, triggered_by, triggered_at_utc, completed_at_utc, error_code,
        error_summary, requested_visibility, published_content_hash, remote_content_hash, remote_checked_at_utc,
        remote_divergence_acknowledged_at_utc, export_round
        """;

    private readonly SqliteConnectionAccessor _accessor;

    public SqlitePublicationRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<Publication?> FindByIdAsync(PublicationId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM publication WHERE id = $id;",
            Map,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    public async Task<Publication?> FindByVersionAndTargetAsync(
        ReflectionVersionId versionId,
        PublishTargetId targetId,
        CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM publication WHERE reflection_version_id = $version AND publish_target_id = $target;",
            Map,
            cancellationToken,
            ("$version", versionId.ToString()),
            ("$target", targetId.ToString())).ConfigureAwait(false);

    public async Task<IReadOnlyList<Publication>> ListByReflectionAsync(
        ReflectionId reflectionId,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {Columns} FROM publication WHERE reflection_id = $reflection ORDER BY scheduled_at_utc DESC;",
            Map,
            cancellationToken,
            ("$reflection", reflectionId.ToString())).ConfigureAwait(false);

    public async Task<IReadOnlyList<Publication>> ListDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {Columns} FROM publication
              WHERE status = $queued AND scheduled_at_utc <= $now
              ORDER BY scheduled_at_utc
              LIMIT $limit;
             """,
            Map,
            cancellationToken,
            ("$queued", (int)PublicationStatus.Queued),
            ("$now", SqliteValues.Instant(nowUtc)),
            ("$limit", limit)).ConfigureAwait(false);

    /// <summary>
    /// Everything not yet finished, for the sweep that expires overdue slots and invalidates versions the draft
    /// has moved past. Deliberately includes in-flight rows so the sweep can skip them explicitly rather than by
    /// omission.
    /// </summary>
    public async Task<IReadOnlyList<Publication>> ListOutstandingAsync(int limit, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {Columns} FROM publication
              WHERE status IN ($queued, $inProgress, $draftUploaded)
              ORDER BY scheduled_at_utc
              LIMIT $limit;
             """,
            Map,
            cancellationToken,
            ("$queued", (int)PublicationStatus.Queued),
            ("$inProgress", (int)PublicationStatus.InProgress),
            ("$draftUploaded", (int)PublicationStatus.DraftUploaded),
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<IReadOnlyList<Publication>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {Columns} FROM publication ORDER BY scheduled_at_utc DESC LIMIT $limit;",
            Map,
            cancellationToken,
            ("$limit", limit)).ConfigureAwait(false);

    public async Task AddAsync(Publication publication, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO publication ({Columns})
             VALUES ($id, $reflection, $version, $target, $trigger, $status, $remoteId,
                     $attempts, $scheduledAt, $triggeredBy, $triggeredAt, $completedAt, $errorCode,
                     $errorSummary, $visibility, $publishedHash, $remoteHash, $remoteCheckedAt,
                     $acknowledgedAt, $round);
             """,
            cancellationToken,
            Common(publication)).ConfigureAwait(false);

    public async Task UpdateAsync(Publication publication, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE publication
               SET status = $status,
                   remote_id = $remoteId,
                   attempt_count = $attempts,
                   scheduled_at_utc = $scheduledAt,
                   trigger_kind = $trigger,
                   triggered_by = $triggeredBy,
                   triggered_at_utc = $triggeredAt,
                   completed_at_utc = $completedAt,
                   error_code = $errorCode,
                   error_summary = $errorSummary,
                   requested_visibility = $visibility,
                   published_content_hash = $publishedHash,
                   remote_content_hash = $remoteHash,
                   remote_checked_at_utc = $remoteCheckedAt,
                   remote_divergence_acknowledged_at_utc = $acknowledgedAt,
                   export_round = $round
             WHERE id = $id;
            """,
            cancellationToken,
            Common(publication)).ConfigureAwait(false);

    private static (string Name, object? Value)[] Common(Publication publication) =>
    [
        ("$id", publication.Id.ToString()),
        ("$reflection", publication.ReflectionId.ToString()),
        ("$version", publication.ReflectionVersionId.ToString()),
        ("$target", publication.PublishTargetId.ToString()),
        ("$trigger", (int)publication.Trigger),
        ("$status", (int)publication.Status),
        ("$remoteId", SqliteValues.TextOrNull(publication.RemoteId)),
        ("$attempts", publication.AttemptCount),
        ("$scheduledAt", SqliteValues.Instant(publication.ScheduledAtUtc)),
        ("$triggeredBy", SqliteValues.TextOrNull(publication.TriggeredBy)),
        ("$triggeredAt", SqliteValues.InstantOrNull(publication.TriggeredAtUtc)),
        ("$completedAt", SqliteValues.InstantOrNull(publication.CompletedAtUtc)),
        ("$errorCode", SqliteValues.TextOrNull(publication.ErrorCode)),
        ("$errorSummary", SqliteValues.TextOrNull(publication.ErrorSummary)),
        ("$visibility", (int)publication.RequestedVisibility),
        ("$publishedHash", SqliteValues.TextOrNull(publication.PublishedContentHash)),
        ("$remoteHash", SqliteValues.TextOrNull(publication.RemoteContentHash)),
        ("$remoteCheckedAt", SqliteValues.InstantOrNull(publication.RemoteCheckedAtUtc)),
        ("$acknowledgedAt", SqliteValues.InstantOrNull(publication.RemoteDivergenceAcknowledgedAtUtc)),
        ("$round", publication.ExportRound),
    ];

    private static Publication Map(SqliteDataReader reader) =>
        Publication.Rehydrate(
            new PublicationId(SqliteIds.Parse(reader.GetString(0))),
            new ReflectionId(SqliteIds.Parse(reader.GetString(1))),
            new ReflectionVersionId(SqliteIds.Parse(reader.GetString(2))),
            new PublishTargetId(SqliteIds.Parse(reader.GetString(3))),
            (PublicationTrigger)reader.GetInt32(4),
            (PublicationStatus)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            SqliteValues.ReadRequiredInstant(reader, 8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            SqliteValues.ReadInstant(reader, 10),
            SqliteValues.ReadInstant(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            (PublicationVisibility)reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            SqliteValues.ReadInstant(reader, 17),
            SqliteValues.ReadInstant(reader, 18),
            reader.GetInt32(19));
}
