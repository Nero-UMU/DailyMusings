using DailyMusings.Domain.Common;
using DailyMusings.Domain.Topics;
using DailyMusings.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// docs/开发指导.md §15.3: migrations run forward automatically at startup, exactly once, and leave the
/// instance on a known schema.
/// </summary>
[TestClass]
public class MigratorTests
{
    [TestMethod]
    public async Task A_fresh_instance_is_migrated_from_nothing()
    {
        await using var database = await TestDatabase.CreateAsync();

        var applied = await new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance)
            .GetAppliedAsync(CancellationToken.None);

        CollectionAssert.Contains(applied.ToArray(), "0001_initial");
    }

    [TestMethod]
    public async Task Migrating_again_is_a_no_op()
    {
        await using var database = await TestDatabase.CreateAsync();
        var migrator = new SqliteMigrator(database.Accessor, NullLogger<SqliteMigrator>.Instance);

        var appliedNow = await migrator.MigrateAsync(CancellationToken.None);

        Assert.AreEqual(0, appliedNow.Count, "A second run must not reapply anything.");

        // The applied set must be stable and complete, without hard-coding how many migrations happen to exist.
        var applied = await migrator.GetAppliedAsync(CancellationToken.None);
        CollectionAssert.Contains(applied.ToArray(), "0001_initial");
        CollectionAssert.Contains(applied.ToArray(), "0002_input_ingestion");

        var again = await migrator.GetAppliedAsync(CancellationToken.None);
        CollectionAssert.AreEqual(applied.ToArray(), again.ToArray());
    }

    [TestMethod]
    public async Task The_schema_enforces_the_promises_the_product_makes()
    {
        await using var database = await TestDatabase.CreateAsync();

        // One reflection per content day (§7).
        await database.Accessor.ExecuteAsync(
            """
            INSERT INTO reflection (id, content_date, status, generation_reason, created_at_utc, updated_at_utc)
            VALUES ('r1', '2026-03-01', 0, 0, '2026-03-01T00:00:00.0000000+00:00', '2026-03-01T00:00:00.0000000+00:00');
            """,
            CancellationToken.None);

        await Assert.ThrowsExceptionAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await database.Accessor.ExecuteAsync(
                """
                INSERT INTO reflection (id, content_date, status, generation_reason, created_at_utc, updated_at_utc)
                VALUES ('r2', '2026-03-01', 0, 0, '2026-03-01T00:00:00.0000000+00:00', '2026-03-01T00:00:00.0000000+00:00');
                """,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Replayed_work_cannot_be_enqueued_twice()
    {
        await using var database = await TestDatabase.CreateAsync();

        const string insert = """
            INSERT INTO processing_job
                (id, job_type, target_id, status, attempt_count, scheduled_at_utc, idempotency_key)
            VALUES ($id, 0, 'input-1', 0, 0, '2026-03-01T00:00:00.0000000+00:00', $key);
            """;

        await database.Accessor.ExecuteAsync(
            insert,
            CancellationToken.None,
            ("$id", "job-1"),
            ("$key", "transcription:input-1"));

        await Assert.ThrowsExceptionAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await database.Accessor.ExecuteAsync(
                insert,
                CancellationToken.None,
                ("$id", "job-2"),
                ("$key", "transcription:input-1")));
    }

    [TestMethod]
    public async Task A_topic_merge_leaves_a_tombstone_rather_than_deleting_the_row()
    {
        await using var database = await TestDatabase.CreateAsync();

        await database.Accessor.ExecuteAsync(
            """
            INSERT INTO topic (id, name, created_at_utc) VALUES ('t1', '工作', '2026-03-01T00:00:00.0000000+00:00');
            INSERT INTO topic (id, name, created_at_utc) VALUES ('t2', '职业', '2026-03-01T00:00:00.0000000+00:00');
            UPDATE topic SET merged_into_id = 't2', merged_at_utc = '2026-03-02T00:00:00.0000000+00:00' WHERE id = 't1';
            """,
            CancellationToken.None);

        Assert.AreEqual(2L, await database.CountAsync("topic"));
    }

    [TestMethod]
    public async Task Content_days_round_trip_through_storage_unchanged()
    {
        await using var database = await TestDatabase.CreateAsync();
        var day = Domain.Time.ContentDate.From(new DateOnly(2026, 3, 7));

        await database.Accessor.ExecuteAsync(
            """
            INSERT INTO reflection (id, content_date, status, generation_reason, created_at_utc, updated_at_utc)
            VALUES ('r1', $day, 0, 0, '2026-03-07T00:00:00.0000000+00:00', '2026-03-07T00:00:00.0000000+00:00');
            """,
            CancellationToken.None,
            ("$day", SqliteValues.ContentDay(day)));

        var read = await database.Accessor.QuerySingleAsync(
            "SELECT content_date FROM reflection WHERE id = 'r1';",
            reader => SqliteValues.ReadContentDay(reader, 0),
            CancellationToken.None);

        Assert.AreEqual(day, read);
    }

    [TestMethod]
    public async Task Topic_ids_stay_stable_across_a_rename()
    {
        await using var database = await TestDatabase.CreateAsync();
        var topic = Topic.Create(TopicId.New(), "工作", DateTimeOffset.UnixEpoch);

        await database.Accessor.ExecuteAsync(
            "INSERT INTO topic (id, name, created_at_utc) VALUES ($id, $name, $at);",
            CancellationToken.None,
            ("$id", topic.Id.ToString()),
            ("$name", topic.Name),
            ("$at", SqliteValues.Instant(topic.CreatedAtUtc)));

        topic.Rename("职业");

        await database.Accessor.ExecuteAsync(
            "UPDATE topic SET name = $name WHERE id = $id;",
            CancellationToken.None,
            ("$id", topic.Id.ToString()),
            ("$name", topic.Name));

        var id = await database.Accessor.QuerySingleAsync(
            "SELECT id FROM topic WHERE name = '职业';",
            reader => reader.GetString(0),
            CancellationToken.None);

        Assert.AreEqual(topic.Id.ToString(), id);
    }
}
