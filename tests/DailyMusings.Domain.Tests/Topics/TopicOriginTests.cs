using DailyMusings.Domain.Common;
using DailyMusings.Domain.Topics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Topics;

/// <summary>
/// Where a topic's name came from (docs/开发指导.md §6.2). Recorded for display only — the origin gives no
/// privileges, which is the whole point of writing it down separately from any permission.
/// </summary>
[TestClass]
public class TopicOriginTests
{
    [TestMethod]
    public void A_topic_created_without_an_origin_belongs_to_the_user()
    {
        var topic = Topic.Create(TopicId.New(), "录音", TestFactory.Noon);

        Assert.AreEqual(TopicOrigin.User, topic.Origin);
    }

    [TestMethod]
    public void A_topic_the_model_named_is_marked_as_such()
    {
        var topic = Topic.Create(TopicId.New(), "巷子", TestFactory.Noon, TopicOrigin.Model);

        Assert.AreEqual(TopicOrigin.Model, topic.Origin);
        Assert.AreEqual("巷子", topic.Name);
    }

    /// <summary>
    /// Renaming is the user adopting a name, not a change of authorship: a model-named topic that the user
    /// renamed is still one the model coined unless they say otherwise, and flipping the flag on rename would
    /// quietly erase the one piece of history this field exists to keep.
    /// </summary>
    [TestMethod]
    public void Renaming_does_not_change_the_origin()
    {
        var topic = Topic.Create(TopicId.New(), "巷子", TestFactory.Noon, TopicOrigin.Model);

        topic.Rename("夜路");

        Assert.AreEqual(TopicOrigin.Model, topic.Origin);
        Assert.AreEqual("夜路", topic.Name);
    }

    [TestMethod]
    public void Rehydrating_restores_the_origin()
    {
        var topic = Topic.Rehydrate(
            TopicId.New(),
            "巷子",
            TestFactory.Noon,
            mergedIntoId: null,
            mergedAtUtc: null,
            TopicOrigin.Model);

        Assert.AreEqual(TopicOrigin.Model, topic.Origin);

        // Rows written before the column existed read as the user's, which is what they were.
        var legacy = Topic.Rehydrate(TopicId.New(), "录音", TestFactory.Noon, null, null);
        Assert.AreEqual(TopicOrigin.User, legacy.Origin);
    }

    [TestMethod]
    public void The_origin_grants_no_privileges_and_a_model_topic_merges_like_any_other()
    {
        var target = Topic.Create(TopicId.New(), "夜路", TestFactory.Noon, TopicOrigin.Model);

        var source = Topic.Create(TopicId.New(), "巷子", TestFactory.Noon, TopicOrigin.Model);
        source.MergeInto(target.Id, TestFactory.Noon);

        Assert.IsTrue(source.IsMerged);
        Assert.AreEqual(target.Id, source.MergedIntoId);

        TestFactory.ThrowsDomain("topic.merged", () => source.Rename("别的"));
    }
}
