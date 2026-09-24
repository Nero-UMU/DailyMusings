using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Reflections;
using DailyMusings.Application.Topics;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Topics;
using DailyMusings.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// Article topics end to end over a real database (docs/开发指导.md §6.2 as revised): the model's names are
/// resolved against the real vocabulary or become new topics, and a topic an article is about cannot be deleted
/// until the article is re-filed.
/// </summary>
[TestClass]
public class ArticleTopicTests
{
    [TestMethod]
    public async Task An_existing_topic_is_reused_however_the_model_spells_it()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        await context.CreateTopic.ExecuteAsync("录音 上传", CancellationToken.None);

        var resolve = new ResolveArticleTopicsUseCase(context.Topics, context.Clock);

        var (primary, secondary) = await resolve.ExecuteAsync(["录音、上传"], CancellationToken.None);

        Assert.IsNotNull(primary, "The name differs only in punctuation, so it is the same topic.");
        Assert.AreEqual(0, secondary.Count);

        var topic = await context.Topics.FindByIdAsync(primary.Value, CancellationToken.None);

        Assert.IsNotNull(topic);
        Assert.AreEqual("录音 上传", topic.Name, "The stored spelling wins; the model's does not rewrite it.");
        Assert.AreEqual(TopicOrigin.User, topic.Origin, "Reusing a topic must not relabel who named it.");

        var all = await context.Topics.ListAsync(includeMerged: false, CancellationToken.None);
        Assert.AreEqual(1, all.Count, "Matching by name must never produce a second topic.");
    }

    [TestMethod]
    public async Task A_name_the_vocabulary_does_not_have_becomes_a_model_topic()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var resolve = new ResolveArticleTopicsUseCase(context.Topics, context.Clock);
        var (primary, _) = await resolve.ExecuteAsync(["巷子"], CancellationToken.None);

        Assert.IsNotNull(primary);

        var topic = await context.Topics.FindByIdAsync(primary.Value, CancellationToken.None);

        Assert.IsNotNull(topic);
        Assert.AreEqual("巷子", topic.Name);
        Assert.AreEqual(
            TopicOrigin.Model,
            topic.Origin,
            "The admin page has to be able to tell which names a person chose and which one a model proposed.");
    }

    [TestMethod]
    public async Task No_suggested_names_means_no_topics_rather_than_an_invented_one()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var resolve = new ResolveArticleTopicsUseCase(context.Topics, context.Clock);

        var (primary, secondary) = await resolve.ExecuteAsync([], CancellationToken.None);

        Assert.IsNull(primary);
        Assert.AreEqual(0, secondary.Count);

        var (nulls, blanks) = await resolve.ExecuteAsync(["  ", null!], CancellationToken.None);

        Assert.IsNull(nulls);
        Assert.AreEqual(0, blanks.Count);

        Assert.AreEqual(
            0,
            (await context.Topics.ListAsync(includeMerged: true, CancellationToken.None)).Count,
            "An empty answer must leave the vocabulary exactly as it was.");
    }

    [TestMethod]
    public async Task At_most_three_topics_are_attached_and_an_unusable_name_is_dropped()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var resolve = new ResolveArticleTopicsUseCase(context.Topics, context.Clock);

        var (primary, secondary) = await resolve.ExecuteAsync(
            ["一", "二", "三", "四", new string('长', 40)],
            CancellationToken.None);

        Assert.IsNotNull(primary);
        Assert.AreEqual(2, secondary.Count, "Three topics in total: the vocabulary is meant to stay browsable.");

        var names = (await context.Topics.ListAsync(false, CancellationToken.None)).Select(topic => topic.Name).ToArray();
        CollectionAssert.DoesNotContain(names, new string('长', 40), "A name too long to be a topic is dropped, not truncated.");
    }

    [TestMethod]
    public async Task A_topic_an_article_is_about_cannot_be_deleted()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("晨跑", CancellationToken.None);

        await context.CaptureTextAsync("今天跑了一圈。");
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);
        await context.Reflections.SetVersionTopicsAsync(version.Id, [topic.Id], CancellationToken.None);

        var delete = new DeleteTopicUseCase(context.Topics, context.UnitOfWork);

        var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            delete.ExecuteAsync(topic.Id, CancellationToken.None));

        Assert.AreEqual("topic.in_use", failure.Code);
        Assert.IsTrue(failure.Message.Contains("内容管理", StringComparison.Ordinal), "The message has to say where to go.");

        Assert.IsNotNull(
            await context.Topics.FindByIdAsync(topic.Id, CancellationToken.None),
            "A refused deletion must leave the topic in place.");
    }

    /// <summary>
    /// §6.2: 删除主题不得删除原始输入. Deleting loses the label, never the material — so the input is unfiled and
    /// its text survives.
    /// </summary>
    [TestMethod]
    public async Task An_unused_topic_is_deleted_and_the_inputs_are_only_unfiled()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("晨跑", CancellationToken.None);
        var entry = await context.CaptureTextAsync("今天跑了一圈。");

        await context.AssignTopics.ExecuteAsync(entry.Id, topic.Id, [], CancellationToken.None);

        var delete = new DeleteTopicUseCase(context.Topics, context.UnitOfWork);
        var deleted = await delete.ExecuteAsync(topic.Id, CancellationToken.None);

        Assert.AreEqual(topic.Id, deleted.Id);
        Assert.IsNull(await context.Topics.FindByIdAsync(topic.Id, CancellationToken.None));

        var after = await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None);

        Assert.IsNotNull(after, "The capture itself must survive the label being deleted.");
        Assert.IsNull(after.PrimaryTopicId);
        Assert.AreEqual(0, after.SecondaryTopicIds.Count);
        Assert.AreEqual("今天跑了一圈。", after.OriginalTranscript);
    }

    /// <summary>
    /// The path the refusal sends the user down: re-file the article, then the topic is free to go.
    /// </summary>
    [TestMethod]
    public async Task Re_filing_an_article_frees_the_topic_for_deletion()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (used, _) = await context.CreateTopic.ExecuteAsync("晨跑", CancellationToken.None);
        var (replacement, _) = await context.CreateTopic.ExecuteAsync("夜跑", CancellationToken.None);

        await context.CaptureTextAsync("今天跑了一圈。");
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);
        await context.Reflections.SetVersionTopicsAsync(version.Id, [used.Id], CancellationToken.None);

        var assign = new AssignReflectionVersionTopicsUseCase(context.Reflections, context.Topics, context.UnitOfWork);

        await assign.ExecuteAsync(context.Today, replacement.Id, [], CancellationToken.None);

        var refreshed = await context.Reflections.FindVersionAsync(version.Id, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { replacement.Id }, refreshed!.TopicIds.ToArray());

        var delete = new DeleteTopicUseCase(context.Topics, context.UnitOfWork);
        await delete.ExecuteAsync(used.Id, CancellationToken.None);

        Assert.IsNull(await context.Topics.FindByIdAsync(used.Id, CancellationToken.None));
        Assert.IsNotNull(
            await context.Topics.FindByIdAsync(replacement.Id, CancellationToken.None),
            "The topic the article moved to is in use, so it stays.");
    }

    [TestMethod]
    public async Task A_merged_topic_cannot_receive_an_article()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (retired, _) = await context.CreateTopic.ExecuteAsync("晨跑", CancellationToken.None);
        var (target, _) = await context.CreateTopic.ExecuteAsync("跑步", CancellationToken.None);

        await context.MergeTopics.ExecuteAsync(retired.Id, target.Id, CancellationToken.None);

        await context.CaptureTextAsync("今天跑了一圈。");
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);
        var assign = new AssignReflectionVersionTopicsUseCase(context.Reflections, context.Topics, context.UnitOfWork);

        var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            assign.ExecuteAsync(context.Today, retired.Id, [], CancellationToken.None));

        Assert.AreEqual("topic.retired", failure.Code);

        // And the usage list says which articles are using a topic, which is what the admin page shows.
        var usage = new GetTopicUsageUseCase(context.Topics);
        await context.Reflections.SetVersionTopicsAsync(version.Id, [target.Id], CancellationToken.None);

        var view = await usage.ExecuteAsync(target.Id, CancellationToken.None);

        Assert.AreEqual(1, view.Articles.Count);
        Assert.AreEqual(context.Today.ToString(), view.Articles[0].ContentDate.ToString());
        Assert.AreEqual(ReflectionStatus.ReviewRequired, view.Articles[0].Status);
    }

    /// <summary>
    /// A topic that only a retained earlier version carries is not something an article is "about" any more, and
    /// the deletion must therefore succeed (§6.2). The versions themselves are not rewritten: they simply stop
    /// pointing at a label the user removed.
    /// </summary>
    [TestMethod]
    public async Task A_topic_only_a_superseded_version_carried_can_be_deleted()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天跑了一圈。");

        // First generation: the article is about 晨跑, on what becomes the permanently kept first version.
        context.Client.Override = _ => DraftAbout("晨跑");
        var first = await context.Generate.ExecuteAsync(context.Today, Payload(), CancellationToken.None);

        var firstVersionId = first.Version!.Id;
        var retired = (await context.Topics.FindByNameAsync("晨跑", CancellationToken.None))!;

        Assert.AreEqual(1, await context.Topics.CountArticleUsagesAsync(retired.Id, CancellationToken.None));

        // Second generation: the day is now about 夜跑, so the earlier version is no longer the current one.
        context.Client.Override = _ => DraftAbout("夜跑");
        var second = await context.Generate.ExecuteAsync(context.Today, Payload(), CancellationToken.None);

        Assert.AreNotEqual(firstVersionId, second.Version!.Id);
        Assert.AreEqual(
            0,
            await context.Topics.CountArticleUsagesAsync(retired.Id, CancellationToken.None),
            "A version the day has moved on from is not a reason to keep the topic.");

        var delete = new DeleteTopicUseCase(context.Topics, context.UnitOfWork);
        await delete.ExecuteAsync(retired.Id, CancellationToken.None);

        Assert.IsNull(await context.Topics.FindByIdAsync(retired.Id, CancellationToken.None));

        // The link rows are gone — otherwise the foreign key would have refused the delete — and the retained
        // version's text and topics-read are otherwise untouched.
        var remaining = await context.Database.Accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM reflection_version_topic WHERE topic_id = $topic;",
            reader => reader.GetInt32(0),
            CancellationToken.None,
            ("$topic", retired.Id.ToString()));

        Assert.AreEqual(0, remaining);

        var retained = await context.Reflections.FindVersionAsync(firstVersionId, CancellationToken.None);

        Assert.IsNotNull(retained, "The first version is kept forever (§6.4); only its label was removed.");
        Assert.AreEqual(0, retained.TopicIds.Count);
        Assert.AreEqual("今天跑了一圈。", retained.Body);

        // The surviving topic is still in use, so it stays.
        var surviving = (await context.Topics.FindByNameAsync("夜跑", CancellationToken.None))!;
        Assert.AreEqual(1, await context.Topics.CountArticleUsagesAsync(surviving.Id, CancellationToken.None));
    }

    private static GeneratedDraft DraftAbout(string topic) => new(
        "今天的记录",
        "把今天的话留下来。",
        "今天跑了一圈。",
        ["记录"],
        ["随想"],
        [],
        [topic],
        []);

    private static ReflectionGenerationPayload Payload() =>
        new(AllowOverwriteOfManualEdits: false, GenerationReason.Manual, IgnoreTranscriptionFailures: false);

    [TestMethod]
    public async Task Generated_topics_are_stored_with_the_version_and_read_back()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天走了一条没走过的巷子。");

        // The fake model names a theme; the generation path resolves it into a real topic (default origin: user,
        // because the vocabulary is empty and the model's name becomes a new model-named topic).
        context.Client.Override = request => new Application.Abstractions.GeneratedDraft(
            "巷子",
            "走了没走过的路。",
            "今天走了一条没走过的巷子。",
            ["记录"],
            ["随想"],
            [],
            ["巷子"],
            []);

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today,
            manual: true,
            ignoreTranscriptionFailures: false,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            Application.Abstractions.ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        Assert.AreEqual(1, result.Version!.TopicIds.Count);

        var topic = await context.Topics.FindByIdAsync(result.Version.TopicIds[0], CancellationToken.None);

        Assert.IsNotNull(topic);
        Assert.AreEqual("巷子", topic.Name);
        Assert.AreEqual(TopicOrigin.Model, topic.Origin);

        // The topic is stored on the version, so the deletion guard can see it.
        Assert.AreEqual(1, await context.Topics.CountArticleUsagesAsync(topic.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_merged_name_resolves_to_the_surviving_topic_instead_of_being_recreated()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天走了一条没走过的巷子。");

        // The user decided these two labels are one theme. A model that still says the retired one must be
        // understood through that decision, not handed a brand-new topic carrying the name the user retired.
        var (retired, _) = await context.CreateTopic.ExecuteAsync("巷子", CancellationToken.None);
        var (target, _) = await context.CreateTopic.ExecuteAsync("夜路", CancellationToken.None);
        await context.MergeTopics.ExecuteAsync(retired.Id, target.Id, CancellationToken.None);

        context.Client.Override = request => new GeneratedDraft(
            "巷子",
            "走了没走过的路。",
            "今天走了一条没走过的巷子。",
            [],
            [],
            [],
            ["巷子"],
            []);

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today,
            manual: true,
            ignoreTranscriptionFailures: false,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);
        CollectionAssert.AreEqual(new[] { target.Id }, result.Version!.TopicIds.ToArray());

        var names = (await context.Topics.ListAsync(includeMerged: true, CancellationToken.None))
            .Select(topic => topic.Name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "巷子", "夜路" }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            names.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            "No third topic may appear under the retired name.");
    }
}
