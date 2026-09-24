using System.Globalization;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Time;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Entries captured by the client (docs/开发指导.md §6.1, §9.2).
/// <para>
/// Secondary topic assignments live in a join table, so a list query loads them in one extra round trip keyed by
/// the entries it just read rather than one query per entry.
/// </para>
/// </summary>
public sealed class SqliteInputEntryRepository : IInputEntryRepository
{
    private const string Columns = """
        id, source_type, created_at_utc, created_offset_minutes, content_date,
        audio_path, audio_content_type, audio_duration_ticks, audio_deleted_at_utc,
        original_transcript, revised_transcript, transcription_status, transcription_error_code,
        allow_future_recall, primary_topic_id, deleted_at_utc, client_idempotency_key, device_id
        """;

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteInputEntryRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<InputEntry?> FindByIdAsync(InputEntryId id, CancellationToken cancellationToken)
    {
        var entries = await LoadAsync(
            $"SELECT {Columns} FROM input_entry WHERE id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

        return entries.Count == 0 ? null : entries[0];
    }

    public async Task<InputEntry?> FindByClientKeyAsync(
        string clientIdempotencyKey,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAsync(
            $"SELECT {Columns} FROM input_entry WHERE client_idempotency_key = $key;",
            cancellationToken,
            ("$key", clientIdempotencyKey)).ConfigureAwait(false);

        return entries.Count == 0 ? null : entries[0];
    }

    public Task<IReadOnlyList<InputEntry>> ListByContentDateAsync(
        ContentDate contentDate,
        CancellationToken cancellationToken) =>
        LoadAsync(
            $"""
             SELECT {Columns} FROM input_entry
              WHERE content_date = $day AND deleted_at_utc IS NULL
              ORDER BY created_at_utc, id;
             """,
            cancellationToken,
            ("$day", SqliteValues.ContentDay(contentDate)));

    public Task<IReadOnlyList<InputEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        // Deleted entries are excluded here and in the day query: a client that deleted a capture must not see it
        // come back in its timeline. Point lookups still resolve them, because the transcription handler needs to
        // notice that its target is gone.
        return LoadAsync(
            $"""
             SELECT {Columns} FROM input_entry
              WHERE deleted_at_utc IS NULL
              ORDER BY created_at_utc DESC, id DESC
              LIMIT $limit;
             """,
            cancellationToken,
            ("$limit", limit));
    }

    /// <summary>
    /// One page of the archive view, newest first. Served by the same ordered index the recent list uses, so a
    /// deep page is not a table scan.
    /// </summary>
    public Task<IReadOnlyList<InputEntry>> ListPageAsync(
        int offset,
        int limit,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        }

        return LoadAsync(
            $"""
             SELECT {Columns} FROM input_entry
              {(includeDeleted ? string.Empty : "WHERE deleted_at_utc IS NULL")}
              ORDER BY created_at_utc DESC, id DESC
              LIMIT $limit OFFSET $offset;
             """,
            cancellationToken,
            ("$limit", limit),
            ("$offset", offset));
    }

    public async Task<int> CountAsync(bool includeDeleted, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"""
             SELECT COUNT(*) FROM input_entry
              {(includeDeleted ? string.Empty : "WHERE deleted_at_utc IS NULL")};
             """,
            reader => reader.GetInt32(0),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Everything a reflection on <paramref name="upToInclusive"/> could cite (docs/开发指导.md §8.3).
    /// <para>
    /// The day boundary is in the SQL, not in the caller. §8.3's "only material from the article's day or
    /// earlier" is the rule that keeps a reflection from describing something as having happened before it did,
    /// and a boundary that lives only in application code is one a later caller can forget to apply.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<InputEntry>> ListRecallCandidatesAsync(
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken) =>
        LoadAsync(
            $"""
             SELECT {Columns} FROM input_entry
              WHERE deleted_at_utc IS NULL
                AND allow_future_recall = 1
                AND content_date <= $upTo
                AND (COALESCE(revised_transcript, original_transcript) IS NOT NULL)
              ORDER BY content_date, created_at_utc, id
              LIMIT $limit;
             """,
            cancellationToken,
            ("$upTo", SqliteValues.ContentDay(upToInclusive)),
            ("$limit", limit));

    /// <summary>
    /// The days that have material, newest first. The catch-up scan walks this rather than a date range so that a
    /// day with nothing in it is never even considered — which is how §7's "当天无输入时不得创建空文章" is kept
    /// true by construction instead of by a check that has to remember to run.
    /// </summary>
    public async Task<IReadOnlyList<ContentDate>> ListContentDatesWithInputsAsync(
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            """
            SELECT DISTINCT content_date FROM input_entry
             WHERE deleted_at_utc IS NULL
               AND content_date <= $upTo
               AND (COALESCE(revised_transcript, original_transcript) IS NOT NULL)
             ORDER BY content_date DESC
             LIMIT $limit;
            """,
            reader => SqliteValues.ReadContentDay(reader, 0),
            cancellationToken,
            ("$upTo", SqliteValues.ContentDay(upToInclusive)),
            ("$limit", limit)).ConfigureAwait(false);

    /// <summary>
    /// Every entry, oldest first, including days that never produced a draft. The export and the backup both promise
    /// completeness (§15.1), and an input on a day with no reflection is still the user's material.
    /// </summary>
    public Task<IReadOnlyList<InputEntry>> ListAllAsync(int limit, CancellationToken cancellationToken) =>
        LoadAsync(
            $"""
             SELECT {Columns} FROM input_entry
              ORDER BY content_date, created_at_utc, id
              LIMIT $limit;
             """,
            cancellationToken,
            ("$limit", limit));

    public async Task AddAsync(InputEntry entry, CancellationToken cancellationToken)
    {
        await _accessor.ExecuteAsync(
            """
            INSERT INTO input_entry
                (id, source_type, created_at_utc, created_offset_minutes, content_date,
                 audio_path, audio_content_type, audio_duration_ticks, audio_deleted_at_utc,
                 original_transcript, revised_transcript, transcription_status, transcription_error_code,
                 allow_future_recall, primary_topic_id, deleted_at_utc, client_idempotency_key, device_id)
            VALUES
                ($id, $sourceType, $createdAt, $offset, $contentDate,
                 $audioPath, $audioContentType, $audioDurationTicks, $audioDeletedAt,
                 $original, $revised, $transcriptionStatus, $transcriptionError,
                 $allowRecall, $primaryTopic, $deletedAt, $clientKey, $deviceId);
            """,
            cancellationToken,
            ("$id", entry.Id.ToString()),
            ("$sourceType", (int)entry.SourceType),
            ("$createdAt", SqliteValues.Instant(entry.CreatedAtUtc)),
            ("$offset", entry.CreatedOffsetMinutes),
            ("$contentDate", SqliteValues.ContentDay(entry.ContentDate)),
            ("$audioPath", SqliteValues.TextOrNull(entry.AudioPath)),
            ("$audioContentType", SqliteValues.TextOrNull(entry.AudioContentType)),
            ("$audioDurationTicks", entry.AudioDuration?.Ticks),
            ("$audioDeletedAt", SqliteValues.InstantOrNull(entry.AudioDeletedAtUtc)),
            ("$original", SqliteValues.TextOrNull(entry.OriginalTranscript)),
            ("$revised", SqliteValues.TextOrNull(entry.RevisedTranscript)),
            ("$transcriptionStatus", (int)entry.TranscriptionStatus),
            ("$transcriptionError", SqliteValues.TextOrNull(entry.TranscriptionErrorCode)),
            ("$allowRecall", entry.AllowFutureRecall ? 1 : 0),
            ("$primaryTopic", SqliteValues.GuidOrNull(entry.PrimaryTopicId?.Value)),
            ("$deletedAt", SqliteValues.InstantOrNull(entry.DeletedAtUtc)),
            ("$clientKey", SqliteValues.TextOrNull(entry.ClientIdempotencyKey)),
            ("$deviceId", SqliteValues.GuidOrNull(entry.DeviceId?.Value))).ConfigureAwait(false);

        await SaveSecondaryTopicsAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(InputEntry entry, CancellationToken cancellationToken)
    {
        await _accessor.ExecuteAsync(
            """
            UPDATE input_entry
               SET audio_path = $audioPath,
                   audio_content_type = $audioContentType,
                   audio_duration_ticks = $audioDurationTicks,
                   audio_deleted_at_utc = $audioDeletedAt,
                   original_transcript = $original,
                   revised_transcript = $revised,
                   transcription_status = $transcriptionStatus,
                   transcription_error_code = $transcriptionError,
                   allow_future_recall = $allowRecall,
                   primary_topic_id = $primaryTopic,
                   deleted_at_utc = $deletedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", entry.Id.ToString()),
            ("$audioPath", SqliteValues.TextOrNull(entry.AudioPath)),
            ("$audioContentType", SqliteValues.TextOrNull(entry.AudioContentType)),
            ("$audioDurationTicks", entry.AudioDuration?.Ticks),
            ("$audioDeletedAt", SqliteValues.InstantOrNull(entry.AudioDeletedAtUtc)),
            ("$original", SqliteValues.TextOrNull(entry.OriginalTranscript)),
            ("$revised", SqliteValues.TextOrNull(entry.RevisedTranscript)),
            ("$transcriptionStatus", (int)entry.TranscriptionStatus),
            ("$transcriptionError", SqliteValues.TextOrNull(entry.TranscriptionErrorCode)),
            ("$allowRecall", entry.AllowFutureRecall ? 1 : 0),
            ("$primaryTopic", SqliteValues.GuidOrNull(entry.PrimaryTopicId?.Value)),
            ("$deletedAt", SqliteValues.InstantOrNull(entry.DeletedAtUtc))).ConfigureAwait(false);

        await SaveSecondaryTopicsAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the entry's secondary topics into the join table.
    /// <para>
    /// Replace rather than merge: the domain value is the whole assignment, so anything the caller removed must
    /// disappear. Without this the assignment would live only in memory, and a restart would silently file every
    /// input under nothing — which would take the degraded retrieval signal with it.
    /// </para>
    /// </summary>
    private async Task SaveSecondaryTopicsAsync(InputEntry entry, CancellationToken cancellationToken)
    {
        await _accessor.ExecuteAsync(
            "DELETE FROM input_entry_secondary_topic WHERE input_entry_id = $id;",
            cancellationToken,
            ("$id", entry.Id.ToString())).ConfigureAwait(false);

        foreach (var topicId in entry.SecondaryTopicIds)
        {
            await _accessor.ExecuteAsync(
                """
                INSERT INTO input_entry_secondary_topic (input_entry_id, topic_id)
                VALUES ($id, $topicId);
                """,
                cancellationToken,
                ("$id", entry.Id.ToString()),
                ("$topicId", topicId.ToString())).ConfigureAwait(false);
        }
    }

    /// <summary>Reads entries plus their secondary topics in two queries, never one per row.</summary>
    private async Task<IReadOnlyList<InputEntry>> LoadAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var rows = await _accessor
            .QueryAsync(sql, ReadRow, cancellationToken, parameters)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return [];
        }

        var secondaries = await LoadSecondaryTopicsAsync(rows.Select(row => row.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => row.ToEntry(secondaries.GetValueOrDefault(row.Id, [])))
            .ToArray();
    }

    private async Task<Dictionary<InputEntryId, List<TopicId>>> LoadSecondaryTopicsAsync(
        IReadOnlyList<InputEntryId> inputIds,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(
            "SELECT input_entry_id, topic_id FROM input_entry_secondary_topic WHERE input_entry_id IN (");
        var parameters = new List<(string, object?)>(inputIds.Count);

        for (var i = 0; i < inputIds.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            // Parameter names are generated, never interpolated from data.
            var name = string.Create(CultureInfo.InvariantCulture, $"$id{i}");
            builder.Append(name);
            parameters.Add((name, inputIds[i].ToString()));
        }

        builder.Append(");");

        var pairs = await _accessor
            .QueryAsync(
                builder.ToString(),
                reader => (Input: SqliteIds.InputEntry(reader.GetString(0)), Topic: SqliteIds.Topic(reader.GetString(1))),
                cancellationToken,
                parameters.ToArray())
            .ConfigureAwait(false);

        var lookup = new Dictionary<InputEntryId, List<TopicId>>();
        foreach (var (input, topic) in pairs)
        {
            if (!lookup.TryGetValue(input, out var list))
            {
                list = [];
                lookup[input] = list;
            }

            list.Add(topic);
        }

        return lookup;
    }

    private static InputRow ReadRow(SqliteDataReader reader) => new(
        Id: SqliteIds.InputEntry(reader.GetString(0)),
        SourceType: (InputSourceType)reader.GetInt32(1),
        CreatedAtUtc: SqliteValues.ReadRequiredInstant(reader, 2),
        CreatedOffsetMinutes: reader.GetInt32(3),
        ContentDate: SqliteValues.ReadContentDay(reader, 4),
        AudioPath: reader.IsDBNull(5) ? null : reader.GetString(5),
        AudioContentType: reader.IsDBNull(6) ? null : reader.GetString(6),
        AudioDurationTicks: reader.IsDBNull(7) ? null : reader.GetInt64(7),
        AudioDeletedAtUtc: SqliteValues.ReadInstant(reader, 8),
        OriginalTranscript: reader.IsDBNull(9) ? null : reader.GetString(9),
        RevisedTranscript: reader.IsDBNull(10) ? null : reader.GetString(10),
        TranscriptionStatus: (TranscriptionStatus)reader.GetInt32(11),
        TranscriptionErrorCode: reader.IsDBNull(12) ? null : reader.GetString(12),
        AllowFutureRecall: SqliteValues.ReadBool(reader, 13),
        PrimaryTopicId: reader.IsDBNull(14) ? null : SqliteIds.Topic(reader.GetString(14)),
        DeletedAtUtc: SqliteValues.ReadInstant(reader, 15),
        ClientIdempotencyKey: reader.IsDBNull(16) ? null : reader.GetString(16),
        DeviceId: reader.IsDBNull(17) ? null : SqliteIds.Device(reader.GetString(17)));

    private sealed record InputRow(
        InputEntryId Id,
        InputSourceType SourceType,
        DateTimeOffset CreatedAtUtc,
        int CreatedOffsetMinutes,
        ContentDate ContentDate,
        string? AudioPath,
        string? AudioContentType,
        long? AudioDurationTicks,
        DateTimeOffset? AudioDeletedAtUtc,
        string? OriginalTranscript,
        string? RevisedTranscript,
        TranscriptionStatus TranscriptionStatus,
        string? TranscriptionErrorCode,
        bool AllowFutureRecall,
        TopicId? PrimaryTopicId,
        DateTimeOffset? DeletedAtUtc,
        string? ClientIdempotencyKey,
        DeviceId? DeviceId)
    {
        public InputEntry ToEntry(IReadOnlyList<TopicId> secondaryTopicIds) =>
            InputEntry.Rehydrate(
                Id,
                SourceType,
                CreatedAtUtc,
                CreatedOffsetMinutes,
                ContentDate,
                AudioPath,
                AudioContentType,
                AudioDurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
                AudioDeletedAtUtc,
                OriginalTranscript,
                RevisedTranscript,
                TranscriptionStatus,
                TranscriptionErrorCode,
                AllowFutureRecall,
                PrimaryTopicId,
                secondaryTopicIds,
                DeletedAtUtc,
                ClientIdempotencyKey,
                DeviceId);
    }
}
