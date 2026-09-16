using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
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
    private const string Columns = "id, name, created_at_utc, merged_into_id, merged_at_utc";

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

    public async Task AddAsync(Topic topic, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            $"""
             INSERT INTO topic ({Columns})
             VALUES ($id, $name, $createdAt, $mergedInto, $mergedAt);
             """,
            cancellationToken,
            ("$id", topic.Id.ToString()),
            ("$name", topic.Name),
            ("$createdAt", SqliteValues.Instant(topic.CreatedAtUtc)),
            ("$mergedInto", SqliteValues.GuidOrNull(topic.MergedIntoId?.Value)),
            ("$mergedAt", SqliteValues.InstantOrNull(topic.MergedAtUtc))).ConfigureAwait(false);

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

    private static Topic Map(SqliteDataReader reader) =>
        Topic.Rehydrate(
            SqliteIds.Topic(reader.GetString(0)),
            reader.GetString(1),
            SqliteValues.ReadRequiredInstant(reader, 2),
            reader.IsDBNull(3) ? null : SqliteIds.Topic(reader.GetString(3)),
            SqliteValues.ReadInstant(reader, 4));
}
