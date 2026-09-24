using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Topics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// An article's topics (docs/开发指导.md §6.2 as revised): the model picks them during generation, the admin
/// page may re-file them, and the first one is the day's main theme.
/// </summary>
[TestClass]
public class VersionTopicTests
{
    [TestMethod]
    public void The_first_topic_is_primary_and_the_rest_are_secondary()
    {
        var reflection = TestFactory.NewReflection();

        var primary = TopicId.New();
        var second = TopicId.New();
        var third = TopicId.New();

        var version = ReflectionVersion.CreateGenerated(
            DailyMusings.Domain.Common.ReflectionVersionId.New(),
            reflection.Id,
            "标题",
            "摘要",
            "正文。",
            WritingSettings.Default,
            new ModelInfo("test-model"),
            "generation-v2",
            TestFactory.Noon,
            topics: [primary, second, third]);

        CollectionAssert.AreEqual(
            new[] { primary, second, third },
            version.TopicIds.ToArray(),
            "The order is the decision: the first topic is what the day was mainly about.");
    }

    [TestMethod]
    public void AttachTopics_drops_duplicates_and_never_lists_the_primary_twice()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);

        var primary = TopicId.New();
        var secondary = TopicId.New();

        // A model that repeats itself must not produce a stored list that says "mainly A, and also A".
        version.AttachTopics(primary, [primary, secondary, secondary, default(TopicId)]);

        CollectionAssert.AreEqual(new[] { primary, secondary }, version.TopicIds.ToArray());
    }

    [TestMethod]
    public void AttachTopics_accepts_secondaries_without_a_primary()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);

        var secondary = TopicId.New();
        version.AttachTopics(null, [secondary]);

        CollectionAssert.AreEqual(
            new[] { secondary },
            version.TopicIds.ToArray(),
            "A day with only weak matches has no main theme, which is a legitimate answer.");
    }

    [TestMethod]
    public void An_article_with_no_topics_reports_none()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);

        Assert.AreEqual(0, version.TopicIds.Count);

        version.AttachTopics(null, []);
        Assert.AreEqual(0, version.TopicIds.Count);
    }

    /// <summary>
    /// Editing the text must not re-file the article. The topics are a judgement about what the day was about,
    /// not a mapping onto the wording — and an edit that silently dropped them would lose a decision the model
    /// made and the user may already have reviewed.
    /// </summary>
    [TestMethod]
    public void Editing_the_text_keeps_the_topics()
    {
        var reflection = TestFactory.NewReflection();
        var topic = TopicId.New();

        var version = ReflectionVersion.CreateGenerated(
            DailyMusings.Domain.Common.ReflectionVersionId.New(),
            reflection.Id,
            "标题",
            "摘要",
            "正文。",
            WritingSettings.Default,
            new ModelInfo("test-model"),
            "generation-v2",
            TestFactory.Noon,
            topics: [topic]);

        version.Edit("新标题", "新摘要", "我自己改过的正文。", TestFactory.Noon);

        CollectionAssert.AreEqual(new[] { topic }, version.TopicIds.ToArray());
        Assert.IsTrue(version.HasManualEdits);
    }

    [TestMethod]
    public void Rehydrating_restores_the_topics_in_their_stored_order()
    {
        var reflection = TestFactory.NewReflection();
        var primary = TopicId.New();
        var secondary = TopicId.New();

        var version = ReflectionVersion.Rehydrate(
            DailyMusings.Domain.Common.ReflectionVersionId.New(),
            reflection.Id,
            "标题",
            "摘要",
            "正文。",
            WritingSettings.Default,
            new ModelInfo("test-model"),
            "generation-v2",
            hasManualEdits: false,
            TestFactory.Noon,
            editedAtUtc: null,
            tags: ["记录"],
            categories: ["随想"],
            sourcesCheckedAtUtc: null,
            topics: [primary, secondary]);

        CollectionAssert.AreEqual(new[] { primary, secondary }, version.TopicIds.ToArray());
    }
}
