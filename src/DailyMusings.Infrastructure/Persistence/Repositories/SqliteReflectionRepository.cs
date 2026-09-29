using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Daily reflections and their versions (docs/开发指导.md §6.3, §6.4, §6.5).
/// <para>
/// Version rows are append-only apart from a user edit: the four slot pointers on the reflection are what move.
/// That is why <see cref="Reflection.ConfirmedVersionId"/> can never dangle and why "the first version is kept
/// forever" holds without any code having to remember it.
/// </para>
/// </summary>
public sealed class SqliteReflectionRepository : IReflectionRepository
{
    private const string ReflectionColumns = """
        id, content_date, status, generation_reason, last_stale_reason,
        initial_version_id, previous_version_id, working_version_id, confirmed_version_id,
        created_at_utc, updated_at_utc, confirmed_at_utc
        """;

    private const string VersionColumns = """
        id, reflection_id, title, summary, body, tags_json, categories_json, settings_json,
        model_name, prompt_version, has_manual_edits, created_at_utc, edited_at_utc, sources_checked_at_utc
        """;

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteReflectionRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<Reflection?> FindByContentDateAsync(ContentDate contentDate, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {ReflectionColumns} FROM reflection WHERE content_date = $day;",
            MapReflection,
            cancellationToken,
            ("$day", SqliteValues.ContentDay(contentDate))).ConfigureAwait(false);

    public async Task<Reflection?> FindByIdAsync(ReflectionId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {ReflectionColumns} FROM reflection WHERE id = $id;",
            MapReflection,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    public async Task<IReadOnlyList<Reflection>> ListByDateRangeAsync(
        ContentDate fromInclusive,
        ContentDate toInclusive,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {ReflectionColumns} FROM reflection
              WHERE content_date >= $from AND content_date <= $to
              ORDER BY content_date DESC;
             """,
            MapReflection,
            cancellationToken,
            ("$from", SqliteValues.ContentDay(fromInclusive)),
            ("$to", SqliteValues.ContentDay(toInclusive))).ConfigureAwait(false);

    public async Task<IReadOnlyList<Reflection>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {ReflectionColumns} FROM reflection ORDER BY content_date DESC LIMIT $limit;",
            MapReflection,
            cancellationToken,
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<int> CountByDateRangeAsync(
        ContentDate fromInclusive,
        ContentDate toInclusive,
        CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM reflection WHERE content_date >= $from AND content_date <= $to;",
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$from", SqliteValues.ContentDay(fromInclusive)),
            ("$to", SqliteValues.ContentDay(toInclusive))).ConfigureAwait(false);

    public async Task<IReadOnlyList<Reflection>> ListAllAsync(int limit, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {ReflectionColumns} FROM reflection ORDER BY content_date LIMIT $limit;",
            MapReflection,
            cancellationToken,
            ("$limit", limit)).ConfigureAwait(false);

    /// <summary>
    /// The retention sweep's query (decision A.1): days whose <em>first confirmation</em> is old enough. Indexed on
    /// that column, so a sweep does not walk every day the instance has ever written.
    /// </summary>
    public async Task<IReadOnlyList<Reflection>> ListConfirmedBeforeAsync(
        DateTimeOffset cutoffUtc,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {ReflectionColumns} FROM reflection
              WHERE confirmed_at_utc IS NOT NULL AND confirmed_at_utc <= $cutoff
              ORDER BY confirmed_at_utc
              LIMIT $limit;
             """,
            MapReflection,
            cancellationToken,
            ("$cutoff", SqliteValues.Instant(cutoffUtc)),
            ("$limit", limit)).ConfigureAwait(false);

    public async Task AddAsync(Reflection reflection, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO reflection ({ReflectionColumns})
             VALUES ($id, $day, $status, $reason, $stale,
                     $initial, $previous, $working, $confirmed, $createdAt, $updatedAt, $confirmedAt);
             """,
            cancellationToken,
            ("$id", reflection.Id.ToString()),
            ("$day", SqliteValues.ContentDay(reflection.ContentDate)),
            ("$status", (int)reflection.Status),
            ("$reason", (int)reflection.GenerationReason),
            ("$stale", reflection.LastStaleReason is { } stale ? (int)stale : DBNull.Value),
            ("$initial", SqliteValues.GuidOrNull(reflection.InitialVersionId?.Value)),
            ("$previous", SqliteValues.GuidOrNull(reflection.PreviousVersionId?.Value)),
            ("$working", SqliteValues.GuidOrNull(reflection.WorkingVersionId?.Value)),
            ("$confirmed", SqliteValues.GuidOrNull(reflection.ConfirmedVersionId?.Value)),
            ("$createdAt", SqliteValues.Instant(reflection.CreatedAtUtc)),
            ("$updatedAt", SqliteValues.Instant(reflection.UpdatedAtUtc)),
            ("$confirmedAt", SqliteValues.InstantOrNull(reflection.ConfirmedAtUtc))).ConfigureAwait(false);

    public async Task UpdateAsync(Reflection reflection, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE reflection
               SET status = $status,
                   generation_reason = $reason,
                   last_stale_reason = $stale,
                   initial_version_id = $initial,
                   previous_version_id = $previous,
                   working_version_id = $working,
                   confirmed_version_id = $confirmed,
                   updated_at_utc = $updatedAt,
                   confirmed_at_utc = $confirmedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", reflection.Id.ToString()),
            ("$status", (int)reflection.Status),
            ("$reason", (int)reflection.GenerationReason),
            ("$stale", reflection.LastStaleReason is { } stale ? (int)stale : DBNull.Value),
            ("$initial", SqliteValues.GuidOrNull(reflection.InitialVersionId?.Value)),
            ("$previous", SqliteValues.GuidOrNull(reflection.PreviousVersionId?.Value)),
            ("$working", SqliteValues.GuidOrNull(reflection.WorkingVersionId?.Value)),
            ("$confirmed", SqliteValues.GuidOrNull(reflection.ConfirmedVersionId?.Value)),
            ("$updatedAt", SqliteValues.Instant(reflection.UpdatedAtUtc)),
            ("$confirmedAt", SqliteValues.InstantOrNull(reflection.ConfirmedAtUtc))).ConfigureAwait(false);

    public async Task<bool> IsDeletedAsync(ContentDate contentDate, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            "SELECT EXISTS(SELECT 1 FROM deleted_reflection WHERE content_date = $day);",
            reader => reader.GetInt32(0) != 0,
            cancellationToken,
            ("$day", SqliteValues.ContentDay(contentDate))).ConfigureAwait(false);

    public async Task DeleteAsync(
        Reflection reflection,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reflection);

        await _accessor.ExecuteAsync(
            "INSERT OR REPLACE INTO deleted_reflection (content_date, deleted_at_utc) VALUES ($day, $at);",
            cancellationToken,
            ("$day", SqliteValues.ContentDay(reflection.ContentDate)),
            ("$at", SqliteValues.Instant(deletedAtUtc))).ConfigureAwait(false);

        // The reflection points at its versions and every version points back. Break the former edge first, then
        // remove publications, versions and finally the day itself. Child rows cascade from those two deletes.
        await _accessor.ExecuteAsync(
            """
            UPDATE reflection
               SET initial_version_id = NULL,
                   previous_version_id = NULL,
                   working_version_id = NULL,
                   confirmed_version_id = NULL
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", reflection.Id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM publication WHERE reflection_id = $id;",
            cancellationToken,
            ("$id", reflection.Id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM reflection_version WHERE reflection_id = $id;",
            cancellationToken,
            ("$id", reflection.Id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM reflection WHERE id = $id;",
            cancellationToken,
            ("$id", reflection.Id.ToString())).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a version together with its source map and its second-stage findings.
    /// <para>
    /// All three always travel together. A source reference is meaningless apart from the exact body it was
    /// produced for (A.6), and a finding list without its check timestamp cannot be told apart from "never
    /// checked" — so there is no overload that returns the version alone.
    /// </para>
    /// </summary>
    public async Task<ReflectionVersion?> FindVersionAsync(
        ReflectionVersionId id,
        CancellationToken cancellationToken)
    {
        var version = await _accessor.QuerySingleAsync(
            $"SELECT {VersionColumns} FROM reflection_version WHERE id = $id;",
            MapVersion,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var sources = await _accessor.QueryAsync(
            """
            SELECT id, reflection_version_id, block_index, char_start, char_end, quote_hash,
                   input_entry_id, relevance, reason, is_historical
              FROM source_reference
             WHERE reflection_version_id = $version
             ORDER BY block_index, char_start, input_entry_id;
            """,
            MapSource,
            cancellationToken,
            ("$version", id.ToString())).ConfigureAwait(false);

        version.AttachSources(sources);

        // The version's topics travel with it for the same reason its sources do: they are a judgement about
        // this exact text, and a caller that got the body without them would have to go and ask again.
        var topics = await ListVersionTopicsAsync([id], cancellationToken).ConfigureAwait(false);
        version.AttachTopics(
            topics.TryGetValue(id, out var attached) && attached.Count > 0 ? attached[0] : null,
            topics.TryGetValue(id, out var rest) ? rest.Skip(1).ToArray() : []);

        var claims = await _accessor.QueryAsync(
            """
            SELECT block_index, char_start, char_end, reason
              FROM unsourced_claim
             WHERE reflection_version_id = $version
             ORDER BY block_index, char_start;
            """,
            reader => new UnsourcedClaim(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3)),
            cancellationToken,
            ("$version", id.ToString())).ConfigureAwait(false);

        // Attaching the findings also stamps the check time, so a version that has never been checked keeps a
        // null timestamp even though its findings list is empty (see ReflectionVersion.AttachUnsourcedClaims).
        if (version.SourcesCheckedAtUtc is { } checkedAt)
        {
            version.AttachUnsourcedClaims(claims, checkedAt);
        }

        return version;
    }

    public async Task<int> CountVersionsAsync(ReflectionId reflectionId, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM reflection_version WHERE reflection_id = $id;",
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$id", reflectionId.ToString())).ConfigureAwait(false);

    public async Task AddVersionAsync(ReflectionVersion version, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO reflection_version ({VersionColumns})
             VALUES ($id, $reflection, $title, $summary, $body, $tags, $categories, $settings,
                     $model, $prompt, $edited, $createdAt, $editedAt, $checkedAt);
             """,
            cancellationToken,
            ("$id", version.Id.ToString()),
            ("$reflection", version.ReflectionId.ToString()),
            ("$title", version.Title),
            ("$summary", version.Summary),
            ("$body", version.Body),
            ("$tags", JsonSerializer.Serialize(version.Tags)),
            ("$categories", JsonSerializer.Serialize(version.Categories)),
            ("$settings", JsonSerializer.Serialize(version.Settings)),
            ("$model", SqliteValues.TextOrNull(version.ModelInfo?.ModelName)),
            ("$prompt", SqliteValues.TextOrNull(version.PromptVersion)),
            ("$edited", version.HasManualEdits ? 1 : 0),
            ("$createdAt", SqliteValues.Instant(version.CreatedAtUtc)),
            ("$editedAt", SqliteValues.InstantOrNull(version.EditedAtUtc)),
            ("$checkedAt", SqliteValues.InstantOrNull(version.SourcesCheckedAtUtc))).ConfigureAwait(false);

    public async Task UpdateVersionAsync(ReflectionVersion version, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE reflection_version
               SET title = $title,
                   summary = $summary,
                   body = $body,
                   tags_json = $tags,
                   categories_json = $categories,
                   settings_json = $settings,
                   has_manual_edits = $edited,
                   edited_at_utc = $editedAt,
                   sources_checked_at_utc = $checkedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", version.Id.ToString()),
            ("$title", version.Title),
            ("$summary", version.Summary),
            ("$body", version.Body),
            ("$tags", JsonSerializer.Serialize(version.Tags)),
            ("$categories", JsonSerializer.Serialize(version.Categories)),
            ("$settings", JsonSerializer.Serialize(version.Settings)),
            ("$edited", version.HasManualEdits ? 1 : 0),
            ("$editedAt", SqliteValues.InstantOrNull(version.EditedAtUtc)),
            ("$checkedAt", SqliteValues.InstantOrNull(version.SourcesCheckedAtUtc))).ConfigureAwait(false);

    public async Task ClearSourcesAndFindingsAsync(
        ReflectionVersionId versionId,
        CancellationToken cancellationToken)
    {
        await _accessor.ExecuteAsync(
            "DELETE FROM source_reference WHERE reflection_version_id = $version;",
            cancellationToken,
            ("$version", versionId.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM unsourced_claim WHERE reflection_version_id = $version;",
            cancellationToken,
            ("$version", versionId.ToString())).ConfigureAwait(false);

        // The check described the text that was just replaced, so the version goes back to "not checked" rather
        // than keeping a clean bill of health for sentences nobody examined.
        await _accessor.ExecuteAsync(
            "UPDATE reflection_version SET sources_checked_at_utc = NULL WHERE id = $id;",
            cancellationToken,
            ("$id", versionId.ToString())).ConfigureAwait(false);
    }

    public async Task ReplaceSourcesAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<SourceReference> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);

        await _accessor.ExecuteAsync(
            "DELETE FROM source_reference WHERE reflection_version_id = $version;",
            cancellationToken,
            ("$version", versionId.ToString())).ConfigureAwait(false);

        foreach (var source in sources)
        {
            await _accessor.ExecuteAsync(
                """
                INSERT INTO source_reference
                    (id, reflection_version_id, block_index, char_start, char_end, quote_hash,
                     input_entry_id, relevance, reason, is_historical)
                VALUES
                    ($id, $version, $block, $start, $end, $hash, $input, $relevance, $reason, $historical);
                """,
                cancellationToken,
                ("$id", source.Id.ToString()),
                ("$version", versionId.ToString()),
                ("$block", source.BlockIndex),
                ("$start", source.CharStart),
                ("$end", source.CharEnd),
                ("$hash", source.QuoteHash),
                ("$input", source.InputId.ToString()),
                ("$relevance", source.Relevance),
                ("$reason", source.Reason),
                ("$historical", source.IsHistorical ? 1 : 0)).ConfigureAwait(false);
        }
    }

    public async Task ReplaceUnsourcedClaimsAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<UnsourcedClaim> claims,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claims);

        await _accessor.ExecuteAsync(
            "DELETE FROM unsourced_claim WHERE reflection_version_id = $version;",
            cancellationToken,
            ("$version", versionId.ToString())).ConfigureAwait(false);

        foreach (var claim in claims)
        {
            await _accessor.ExecuteAsync(
                """
                INSERT INTO unsourced_claim (reflection_version_id, block_index, char_start, char_end, reason)
                VALUES ($version, $block, $start, $end, $reason);
                """,
                cancellationToken,
                ("$version", versionId.ToString()),
                ("$block", claim.BlockIndex),
                ("$start", claim.CharStart),
                ("$end", claim.CharEnd),
                ("$reason", claim.Reason)).ConfigureAwait(false);
        }

        // The stamp is the difference between "checked and clean" and "never checked", so it is written with the
        // findings in every case — including when there are none.
        await _accessor.ExecuteAsync(
            "UPDATE reflection_version SET sources_checked_at_utc = $at WHERE id = $id;",
            cancellationToken,
            ("$at", SqliteValues.Instant(checkedAtUtc)),
            ("$id", versionId.ToString())).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a version's topics. Primary first, and the order is persisted because it is the only place the
    /// primary/secondary distinction lives — a separate flag could disagree with the sequence, an ordered list
    /// cannot.
    /// </summary>
    public async Task SetVersionTopicsAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<TopicId> topicIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topicIds);

        await _accessor.ExecuteAsync(
            "DELETE FROM reflection_version_topic WHERE reflection_version_id = $version;",
            cancellationToken,
            ("$version", versionId.ToString())).ConfigureAwait(false);

        var createdAt = SqliteValues.Instant(DateTimeOffset.UtcNow);

        for (var index = 0; index < topicIds.Count; index++)
        {
            if (topicIds[index].IsEmpty)
            {
                continue;
            }

            // INSERT OR IGNORE rather than a plain insert: the primary key is (version, topic), and a caller
            // that passed the same topic twice should get one row rather than a constraint failure that aborts
            // the whole re-filing.
            await _accessor.ExecuteAsync(
                """
                INSERT OR IGNORE INTO reflection_version_topic
                    (reflection_version_id, topic_id, is_primary, created_at_utc)
                VALUES ($version, $topic, $isPrimary, $createdAt);
                """,
                cancellationToken,
                ("$version", versionId.ToString()),
                ("$topic", topicIds[index].ToString()),
                ("$isPrimary", index == 0 ? 1 : 0),
                ("$createdAt", createdAt)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<ReflectionVersionId, IReadOnlyList<TopicId>>> ListVersionTopicsAsync(
        IReadOnlyList<ReflectionVersionId> versionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionIds);

        var result = new Dictionary<ReflectionVersionId, IReadOnlyList<TopicId>>();

        if (versionIds.Count == 0)
        {
            return result;
        }

        var parameters = new (string, object?)[versionIds.Count];
        var builder = new System.Text.StringBuilder(
            "SELECT reflection_version_id, topic_id FROM reflection_version_topic WHERE reflection_version_id IN (");

        for (var i = 0; i < versionIds.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"$v{i}");
            builder.Append(name);
            parameters[i] = (name, versionIds[i].ToString());
        }

        // is_primary first, then insertion order: the primary topic is what the day was mainly about, and a
        // reader that takes the first entry has to get the right one.
        builder.Append(") ORDER BY reflection_version_id, is_primary DESC, created_at_utc, topic_id;");

        var rows = await _accessor.QueryAsync(
            builder.ToString(),
            reader => (
                Version: new ReflectionVersionId(SqliteIds.Parse(reader.GetString(0))),
                Topic: SqliteIds.Topic(reader.GetString(1))),
            cancellationToken,
            parameters).ConfigureAwait(false);

        var grouped = new Dictionary<ReflectionVersionId, List<TopicId>>();

        foreach (var (version, topic) in rows)
        {
            if (!grouped.TryGetValue(version, out var list))
            {
                list = [];
                grouped[version] = list;
            }

            list.Add(topic);
        }

        foreach (var (version, list) in grouped)
        {
            result[version] = list;
        }

        return result;
    }

    private static Reflection MapReflection(SqliteDataReader reader) =>
        Reflection.Rehydrate(
            new ReflectionId(SqliteIds.Parse(reader.GetString(0))),
            SqliteValues.ReadContentDay(reader, 1),
            (ReflectionStatus)reader.GetInt32(2),
            (GenerationReason)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (StaleReason)reader.GetInt32(4),
            reader.IsDBNull(5) ? null : new ReflectionVersionId(SqliteIds.Parse(reader.GetString(5))),
            reader.IsDBNull(6) ? null : new ReflectionVersionId(SqliteIds.Parse(reader.GetString(6))),
            reader.IsDBNull(7) ? null : new ReflectionVersionId(SqliteIds.Parse(reader.GetString(7))),
            reader.IsDBNull(8) ? null : new ReflectionVersionId(SqliteIds.Parse(reader.GetString(8))),
            SqliteValues.ReadRequiredInstant(reader, 9),
            SqliteValues.ReadRequiredInstant(reader, 10),
            SqliteValues.ReadInstant(reader, 11));

    private static ReflectionVersion MapVersion(SqliteDataReader reader) =>
        ReflectionVersion.Rehydrate(
            new ReflectionVersionId(SqliteIds.Parse(reader.GetString(0))),
            new ReflectionId(SqliteIds.Parse(reader.GetString(1))),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ReadSettings(reader.GetString(7)),
            reader.IsDBNull(8) ? null : new ModelInfo(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            SqliteValues.ReadBool(reader, 10),
            SqliteValues.ReadRequiredInstant(reader, 11),
            SqliteValues.ReadInstant(reader, 12),
            ReadStrings(reader.GetString(5)),
            ReadStrings(reader.GetString(6)),
            SqliteValues.ReadInstant(reader, 13));

    private static SourceReference MapSource(SqliteDataReader reader) =>
        SourceReference.Rehydrate(
            new SourceReferenceId(SqliteIds.Parse(reader.GetString(0))),
            new ReflectionVersionId(SqliteIds.Parse(reader.GetString(1))),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            SqliteIds.InputEntry(reader.GetString(6)),
            reader.GetDouble(7),
            reader.GetString(8),
            SqliteValues.ReadBool(reader, 9));

    private static WritingSettings ReadSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return WritingSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<WritingSettings>(json);

            // A blob written before decision A.24 has the old shape (a length enum, a tone string and a bool); one
            // written before A.28 has a single target length instead of a range. Both deserialize *without* error
            // into something that is not a spec at all — so "no usable range" is the test, and it also covers the
            // next shape change instead of only the ones that already happened.
            return settings is null || !IsUsableWritingSettings(settings) ? WritingSettings.Default : settings;
        }
        catch (JsonException)
        {
            // A unreadable settings blob must not make the whole draft unopenable; the defaults are what the
            // instance would have used anyway.
            return WritingSettings.Default;
        }
    }

    /// <summary>
    /// A spec is usable only when it describes a range this version understands: a blob from before A.28 (a single
    /// target length) deserializes into zeros, which must not be handed to a model as if it were a spec.
    /// </summary>
    private static bool IsUsableWritingSettings(WritingSettings settings) =>
        settings.MinCharacters >= WritingSettings.MinAllowedCharacters &&
        settings.MaxCharacters > settings.MinCharacters;

    private static IReadOnlyList<string> ReadStrings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
