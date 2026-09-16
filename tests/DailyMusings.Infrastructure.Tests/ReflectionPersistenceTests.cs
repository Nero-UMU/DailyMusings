using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The draft-side schema: versions, the four slot pointers, the source map and the second-stage findings
/// (docs/开发指导.md §6.3–§6.5).
/// <para>
/// Tested against the real migrations rather than a substitute, because several of these guarantees are made by
/// the schema itself — a tombstoned topic, a version that is never deleted, a source map keyed to a version.
/// </para>
/// </summary>
[TestClass]
public class ReflectionPersistenceTests
{
    [TestMethod]
    public async Task A_draft_round_trips_with_its_versions_and_slot_pointers()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        var day = context.Today;

        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (seeded, version) = await context.SeedDraftAsync(day, ReflectionStatus.ReviewRequired);

        var loaded = await context.Reflections.FindByContentDateAsync(day, CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(seeded.Id, loaded.Id);
        Assert.AreEqual(ReflectionStatus.ReviewRequired, loaded.Status);
        Assert.AreEqual(version.Id, loaded.InitialVersionId);
        Assert.AreEqual(version.Id, loaded.WorkingVersionId);
        Assert.IsNull(loaded.PreviousVersionId);
        Assert.IsNull(loaded.ConfirmedVersionId);

        var loadedVersion = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);
        Assert.IsNotNull(loadedVersion);
        Assert.AreEqual("标题", loadedVersion.Title);
        Assert.AreEqual(version.Body, loadedVersion.Body);
        CollectionAssert.AreEqual(new[] { "记录" }, loadedVersion.Tags.ToArray());
    }

    [TestMethod]
    public async Task The_source_map_survives_a_round_trip_with_its_quote_hash()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        var loaded = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Sources.Count);

        var source = loaded.Sources[0];
        Assert.AreEqual(0, source.BlockIndex);
        Assert.AreEqual("第一段内容", snippet(loaded.Body, source));

        // The stored hash is what lets the client detect that an edited paragraph no longer says what it said
        // (decision A.6), so it has to survive storage exactly.
        CollectionAssert.AreEqual(
            new[] { SourceDrift.Exact },
            loaded.CheckSourceDrift().Select(result => result.Drift).ToArray());

        static string snippet(string body, SourceReference source) =>
            ParagraphSplitter.Split(body)[source.BlockIndex][source.CharStart..source.CharEnd];
    }

    [TestMethod]
    public async Task An_edit_clears_the_provenance_that_described_the_old_text()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (reflection, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        // Edited through the real path, so the storage side of the rule is exercised too.
        var view = await context.EditVersion.ExecuteAsync(
            context.Today,
            "标题",
            "摘要",
            "整段都换掉了。\n\n第二段内容。",
            CancellationToken.None);

        var reloaded = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);

        Assert.IsNotNull(reloaded);
        Assert.IsTrue(reloaded.HasManualEdits);

        // Keeping the map would make the verification screen point at sentences that no longer exist (A.6).
        Assert.AreEqual(0, reloaded.Sources.Count);
        Assert.AreEqual(0, reloaded.UnsourcedClaims.Count);
        Assert.IsNull(reloaded.SourcesCheckedAtUtc, "The check described the old text, so it no longer applies.");
        Assert.AreEqual(0, view.WorkingVersion!.Sources.Count);

        // The draft's slot pointers are untouched by an edit: the same version stays in the working slot.
        var reread = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(reflection.Id, reread!.Id);
        Assert.AreEqual(version.Id, reread.WorkingVersionId);
        Assert.AreEqual(version.Id, reread.InitialVersionId);
    }

    [TestMethod]
    public async Task The_version_count_is_what_makes_a_regeneration_a_new_round()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (reflection, _) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        Assert.AreEqual(1, await context.Reflections.CountVersionsAsync(reflection.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task Findings_and_their_check_timestamp_are_stored_together()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        await context.Reflections.ReplaceUnsourcedClaimsAsync(
            version.Id,
            [new UnsourcedClaim(1, 0, 4, "素材里没有提到")],
            context.Clock.UtcNow,
            CancellationToken.None);

        var loaded = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.UnsourcedClaims.Count);
        Assert.AreEqual("素材里没有提到", loaded.UnsourcedClaims[0].Reason);
        Assert.AreEqual(context.Clock.UtcNow, loaded.SourcesCheckedAtUtc);

        // An empty result is the interesting case: it must read as "checked and clean", not as "never checked".
        await context.Reflections.ReplaceUnsourcedClaimsAsync(
            version.Id,
            [],
            context.Clock.UtcNow,
            CancellationToken.None);

        var clean = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);
        Assert.AreEqual(0, clean!.UnsourcedClaims.Count);
        Assert.IsNotNull(clean.SourcesCheckedAtUtc);
    }

    /// <summary>
    /// docs/开发指导.md §17.1: 主题的合并、重命名与删除不得改变历史文章的来源映射 (decision A.9).
    /// <para>
    /// The invariant holds because <c>source_reference</c> cites inputs, never topics. This test exists so that a
    /// later change which "helpfully" re-points citations during a merge fails loudly.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Merging_topics_never_touches_a_historical_source_map()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (source, _) = await context.CreateTopic.ExecuteAsync("录音上传", CancellationToken.None);
        var (target, _) = await context.CreateTopic.ExecuteAsync("录音", CancellationToken.None);

        var entry = await context.CaptureTextAsync("今天试着记录了一点东西。");
        await context.AssignTopics.ExecuteAsync(entry.Id, source.Id, [], CancellationToken.None);

        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        var before = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);
        var beforeHashes = before!.Sources.Select(item => item.QuoteHash).ToArray();
        var beforeInputs = before.Sources.Select(item => item.InputId).ToArray();

        var (_, _, remapped) = await context.MergeTopics.ExecuteAsync(source.Id, target.Id, CancellationToken.None);

        Assert.AreEqual(1, remapped, "The entry filed under the merged topic must change hands.");

        var after = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);
        CollectionAssert.AreEqual(beforeHashes, after!.Sources.Select(item => item.QuoteHash).ToArray());
        CollectionAssert.AreEqual(beforeInputs, after.Sources.Select(item => item.InputId).ToArray());

        // The merged topic survives as a tombstone so nothing that referenced it can dangle.
        var merged = await context.Topics.FindByIdAsync(source.Id, CancellationToken.None);
        Assert.IsNotNull(merged);
        Assert.IsTrue(merged.IsMerged);
        Assert.AreEqual(target.Id, merged.MergedIntoId);
    }

    [TestMethod]
    public async Task A_merge_re_points_primary_and_secondary_assignments()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (source, _) = await context.CreateTopic.ExecuteAsync("旧主题", CancellationToken.None);
        var (target, _) = await context.CreateTopic.ExecuteAsync("新主题", CancellationToken.None);

        var primary = await context.CaptureTextAsync("第一条记录。", idempotencyKey: "a");
        var secondary = await context.CaptureTextAsync("第二条记录。", idempotencyKey: "b");

        await context.AssignTopics.ExecuteAsync(primary.Id, source.Id, [], CancellationToken.None);
        await context.AssignTopics.ExecuteAsync(secondary.Id, null, [source.Id], CancellationToken.None);

        await context.MergeTopics.ExecuteAsync(source.Id, target.Id, CancellationToken.None);

        var reloadedPrimary = await context.Inputs.FindByIdAsync(primary.Id, CancellationToken.None);
        var reloadedSecondary = await context.Inputs.FindByIdAsync(secondary.Id, CancellationToken.None);

        Assert.AreEqual(target.Id, reloadedPrimary!.PrimaryTopicId);
        CollectionAssert.AreEqual(new[] { target.Id }, reloadedSecondary!.SecondaryTopicIds.ToArray());
    }

    [TestMethod]
    public async Task Renaming_a_topic_keeps_its_identity_and_its_assignments()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("录音", CancellationToken.None);
        var entry = await context.CaptureTextAsync("今天录了一段音。");
        await context.AssignTopics.ExecuteAsync(entry.Id, topic.Id, [], CancellationToken.None);

        var renamed = await context.Topics.FindByIdAsync(topic.Id, CancellationToken.None);
        renamed!.Rename("录音与上传");
        await context.Topics.UpdateAsync(renamed, CancellationToken.None);

        var reloaded = await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None);
        Assert.AreEqual(topic.Id, reloaded!.PrimaryTopicId, "A rename must not move a single assignment (A.9).");
        Assert.AreEqual("录音与上传", (await context.Topics.FindByIdAsync(topic.Id, CancellationToken.None))!.Name);
    }

    [TestMethod]
    public async Task Creating_a_topic_twice_by_an_equivalent_name_returns_the_same_topic()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (first, created) = await context.CreateTopic.ExecuteAsync("录音 上传", CancellationToken.None);
        var (second, createdAgain) = await context.CreateTopic.ExecuteAsync("录音、上传", CancellationToken.None);

        Assert.IsTrue(created);
        Assert.IsFalse(createdAgain, "Spacing and punctuation must not create a second topic.");
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(1, await context.Database.CountAsync("topic"));
    }
}
