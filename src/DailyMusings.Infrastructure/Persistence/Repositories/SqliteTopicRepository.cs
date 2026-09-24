using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Topics;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// The topic vocabulary (docs/开发指导.md §6.2, decision A.9).
/// <para>
/// Merged topics are tombstones that stay in the table forever, so a rename or a merge can never break a
/// client's cached list and a retired id can never be reused with a different meaning.
/// </para>
/// </summary>
public sealed class SqliteTopicRepository : ITopicRepository
{
    private const string Columns = "id, name, created_at_utc, merged_into_id, merged_at_utc, origin";

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteTopicRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<IReadOnlyList<Topic>> ListAsync(bool includeMerged, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {Columns} FROM topic
              {(includeMerged ? string.Empty : "WHERE merged_into_id IS NULL")}
              ORDER BY name, id;
             """,
            Map,
            cancellationToken).ConfigureAwait(false);

    public async Task<Topic?> FindByIdAsync(TopicId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM topic WHERE id = $id;",
            Map,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    /// <summary>
    /// Name lookup is done in memory rather than in SQL.
    /// <para>
    /// The comparison has to ignore case, spacing and punctuation so that "录音 上传" and "录音、上传" are one
    /// topic. SQLite's <c>LOWER</c> is ASCII-only and <c>REPLACE</c> chains would be worse; the vocabulary is a
    /// handful of rows a person curates, so loading them and comparing with the same normalization the matcher
    /// uses is both simpler and more honest than a query that pretends to do the same thing.
    /// </para>
    /// </summary>
    public async Task<Topic?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var wanted = TopicMatcher.Normalize(name);
        if (wanted.Length == 0)
        {
            return null;
        }

        var topics = await ListAsync(includeMerged: false, cancellationToken).ConfigureAwait(false);

        return topics.FirstOrDefault(topic =>
            string.Equals(TopicMatcher.Normalize(topic.Name), wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// Name lookup that follows a merge tombstone to whatever it was merged into (decision A.9).
    /// <para>
    /// A merge is the user saying "these two labels are one theme"; resolving the retired label onto a brand-new
    /// topic with the same name would undo exactly that, one generation later and without asking.
    /// </para>
    /// </summary>
    public async Task<Topic?> FindActiveByNameFollowingMergesAsync(string name, CancellationToken cancellationToken)
    {
        var wanted = TopicMatcher.Normalize(name);
        if (wanted.Length == 0)
        {
            return null;
        }

        // Tombstones included, so the retired label is recognized at all.
        var topics = await ListAsync(includeMerged: true, cancellationToken).ConfigureAwait(false);

        var match = topics.FirstOrDefault(topic =>
            string.Equals(TopicMatcher.Normalize(topic.Name), wanted, StringComparison.Ordinal));

        if (match is null)
        {
            return null;
        }

        // The chain is walked with a visited set: merges cannot form a cycle through the domain, but a lookup
        // that could spin forever on a hand-edited database is not worth leaving to that assumption.
        var visited = new HashSet<TopicId>();

        while (match.IsMerged && match.MergedIntoId is { } target && visited.Add(match.Id))
        {
            var next = topics.FirstOrDefault(topic => topic.Id == target)
                ?? await FindByIdAsync(target, cancellationToken).ConfigureAwait(false);

            if (next is null)
            {
                // The target is gone (only possible if a row was removed outside the API). Reporting "no such
                // topic" is honest; inventing a replacement would not be.
                return null;
            }

            match = next;
        }

        return match.IsMerged ? null : match;
    }

    public async Task AddAsync(Topic topic, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO topic ({Columns})
             VALUES ($id, $name, $createdAt, $mergedInto, $mergedAt, $origin);
             """,
            cancellationToken,
            ("$id", topic.Id.ToString()),
            ("$name", topic.Name),
            ("$createdAt", SqliteValues.Instant(topic.CreatedAtUtc)),
            ("$mergedInto", SqliteValues.GuidOrNull(topic.MergedIntoId?.Value)),
            ("$mergedAt", SqliteValues.InstantOrNull(topic.MergedAtUtc)),
            ("$origin", (int)topic.Origin)).ConfigureAwait(false);

    public async Task UpdateAsync(Topic topic, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE topic
               SET name = $name,
                   merged_into_id = $mergedInto,
                   merged_at_utc = $mergedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", topic.Id.ToString()),
            ("$name", topic.Name),
            ("$mergedInto", SqliteValues.GuidOrNull(topic.MergedIntoId?.Value)),
            ("$mergedAt", SqliteValues.InstantOrNull(topic.MergedAtUtc))).ConfigureAwait(false);

    /// <summary>
    /// Re-points input assignments from one topic to another (decision A.9).
    /// <para>
    /// Note what is deliberately absent: nothing here touches <c>source_reference</c>. Those rows cite inputs,
    /// never topics, so a merge, a rename or a retirement cannot move a historical article's citations. §17.1
    /// asks for that to be a fixed invariant, and a test asserts it.
    /// </para>
    /// </summary>
    public async Task<int> RemapInputsAsync(TopicId from, TopicId to, CancellationToken cancellationToken)
    {
        var primary = await _accessor.ExecuteAsync(
            """
            UPDATE input_entry
               SET primary_topic_id = $to
             WHERE primary_topic_id = $from;
            """,
            cancellationToken,
            ("$to", to.ToString()),
            ("$from", from.ToString())).ConfigureAwait(false);

        // OR IGNORE: an input filed under both topics already has the target row, and the join table's primary
        // key makes the duplicate an error rather than a no-op.
        var secondary = await _accessor.ExecuteAsync(
            """
            INSERT OR IGNORE INTO input_entry_secondary_topic (input_entry_id, topic_id)
            SELECT input_entry_id, $to FROM input_entry_secondary_topic WHERE topic_id = $from;
            """,
            cancellationToken,
            ("$to", to.ToString()),
            ("$from", from.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM input_entry_secondary_topic WHERE topic_id = $from;",
            cancellationToken,
            ("$from", from.ToString())).ConfigureAwait(false);

        // An input can now list the target as both primary and secondary; §6.2 allows only one primary and never
        // the primary as a secondary.
        await _accessor.ExecuteAsync(
            """
            DELETE FROM input_entry_secondary_topic
             WHERE topic_id = $to
               AND input_entry_id IN (SELECT id FROM input_entry WHERE primary_topic_id = $to);
            """,
            cancellationToken,
            ("$to", to.ToString())).ConfigureAwait(false);

        return Math.Max(primary, secondary);
    }

    /// <summary>
    /// Counts the <em>days</em> that are currently about this topic: the working version or the confirmed one
    /// carries it.
    /// <para>
    /// Not "every version that ever carried it". A regenerated day keeps its earlier versions forever (§6.4), so
    /// counting those would let a topic that nothing is about any more block its own deletion for good — and the
    /// admin page, which lists and re-files the current version, would have nothing to offer against it.
    /// </para>
    /// </summary>
    public async Task<int> CountArticleUsagesAsync(TopicId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            """
            SELECT COUNT(*)
              FROM reflection r
             WHERE EXISTS (SELECT 1 FROM reflection_version_topic t
                            WHERE t.topic_id = $topic AND t.reflection_version_id = r.working_version_id)
                OR EXISTS (SELECT 1 FROM reflection_version_topic t
                            WHERE t.topic_id = $topic AND t.reflection_version_id = r.confirmed_version_id);
            """,
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

    /// <summary>
    /// The articles currently about the topic, one row per day. Where a day's confirmed and working versions
    /// both carry it, the confirmed one is reported: that is the text a reader would find, and it is the one the
    /// retention and publication records refer to.
    /// </summary>
    public async Task<IReadOnlyList<TopicArticleUsage>> ListArticleUsagesAsync(
        TopicId id,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            """
            SELECT r.id, r.content_date, rv.id, rv.title, r.status,
                   (SELECT p.status
                      FROM publication p
                     WHERE p.reflection_version_id = rv.id
                     ORDER BY CASE p.status WHEN 3 THEN 0 WHEN 2 THEN 1 ELSE 2 END, p.scheduled_at_utc DESC
                     LIMIT 1)
              FROM reflection r
              JOIN reflection_version rv ON rv.id =
                   (SELECT t.reflection_version_id
                      FROM reflection_version_topic t
                     WHERE t.topic_id = $topic
                       AND t.reflection_version_id IN (r.working_version_id, r.confirmed_version_id)
                     ORDER BY CASE WHEN t.reflection_version_id = r.confirmed_version_id THEN 0 ELSE 1 END
                     LIMIT 1)
             ORDER BY r.content_date, r.id;
            """,
            reader => new TopicArticleUsage(
                new ReflectionId(SqliteIds.Parse(reader.GetString(0))),
                SqliteValues.ReadContentDay(reader, 1),
                new ReflectionVersionId(SqliteIds.Parse(reader.GetString(2))),
                reader.GetString(3),
                (ReflectionStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : (PublicationStatus)reader.GetInt32(5)),
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

    /// <summary>
    /// Inputs filed under the topic, primary and secondary together. The two are counted separately because the
    /// schema stores them separately (one column, one join table) and an input is never in both — the assignment
    /// rule removes the primary from the secondary list.
    /// </summary>
    public async Task<int> CountInputUsagesAsync(TopicId id, CancellationToken cancellationToken)
    {
        var primary = await _accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM input_entry WHERE primary_topic_id = $topic;",
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

        var secondary = await _accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM input_entry_secondary_topic WHERE topic_id = $topic;",
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

        return primary + secondary;
    }

    /// <summary>
    /// Unfiles the topic from every input. The entries themselves are untouched — §6.2's 删除主题不得删除原始输入
    /// is about the material, and losing a label is not losing a thought.
    /// </summary>
    public async Task ClearInputAssignmentsAsync(TopicId id, CancellationToken cancellationToken)
    {
        await _accessor.ExecuteAsync(
            "UPDATE input_entry SET primary_topic_id = NULL WHERE primary_topic_id = $topic;",
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM input_entry_secondary_topic WHERE topic_id = $topic;",
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);
    }

    public async Task DeleteAsync(TopicId id, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            "DELETE FROM topic WHERE id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    /// <summary>
    /// Detaches the topic from every article version, including the ones a day has since moved on from.
    /// <para>
    /// The trade the caller makes, stated once here: a topic the user explicitly deleted stops appearing as a
    /// label on historical versions. Their bodies and their source maps are untouched — nothing about what was
    /// written changes, only the filing that pointed at a row which no longer exists. The alternative would be to
    /// refuse the deletion for as long as any version ever carried the topic, and since the first version of a day
    /// is kept forever (§6.4) that would mean never.
    /// </para>
    /// </summary>
    public async Task ClearVersionTopicLinksAsync(TopicId id, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            "DELETE FROM reflection_version_topic WHERE topic_id = $topic;",
            cancellationToken,
            ("$topic", id.ToString())).ConfigureAwait(false);

    private static Topic Map(SqliteDataReader reader) =>
        Topic.Rehydrate(
            SqliteIds.Topic(reader.GetString(0)),
            reader.GetString(1),
            SqliteValues.ReadRequiredInstant(reader, 2),
            reader.IsDBNull(3) ? null : SqliteIds.Topic(reader.GetString(3)),
            SqliteValues.ReadInstant(reader, 4),

            // Rows written before topics could come from the model carry 0, which is exactly what they were.
            (TopicOrigin)reader.GetInt32(5));
}
