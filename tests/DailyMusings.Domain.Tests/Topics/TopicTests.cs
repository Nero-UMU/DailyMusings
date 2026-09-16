using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Topics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Topics;

/// <summary>
/// docs/开发指导.md §6.2 and decision A.9. The invariant that matters is negative: reorganizing topics
/// must never disturb the source map of an already-written reflection, because source references point
/// at inputs, not at topics.
/// </summary>
[TestClass]
public class TopicTests
{
    [TestMethod]
    public void Renaming_changes_the_label_only()
    {
        var topic = Topic.Create(TopicId.New(), "工作", TestFactory.Noon);
        var id = topic.Id;

        topic.Rename("  职业  ");

        Assert.AreEqual("职业", topic.Name);
        Assert.AreEqual(id, topic.Id);
        Assert.IsFalse(topic.IsMerged);
    }

    [TestMethod]
    public void Merging_retires_the_topic_without_deleting_it()
    {
        var source = Topic.Create(TopicId.New(), "工作", TestFactory.Noon);
        var target = Topic.Create(TopicId.New(), "职业", TestFactory.Noon);

        source.MergeInto(target.Id, TestFactory.Noon);

        Assert.IsTrue(source.IsMerged);
        Assert.AreEqual(target.Id, source.MergedIntoId);
        Assert.AreEqual(TestFactory.Noon, source.MergedAtUtc);
        Assert.AreEqual("工作", source.Name, "the retired row keeps its name for the audit trail");
    }

    [TestMethod]
    public void A_retired_topic_refuses_further_changes()
    {
        var source = Topic.Create(TopicId.New(), "工作", TestFactory.Noon);
        source.MergeInto(TopicId.New(), TestFactory.Noon);

        TestFactory.ThrowsDomain("topic.merged", () => source.Rename("新名字"));
        TestFactory.ThrowsDomain("topic.merged", () => source.MergeInto(TopicId.New(), TestFactory.Noon));
    }

    [TestMethod]
    public void A_topic_cannot_be_merged_into_itself()
    {
        var topic = Topic.Create(TopicId.New(), "工作", TestFactory.Noon);

        TestFactory.ThrowsDomain("topic.merge.self", () => topic.MergeInto(topic.Id, TestFactory.Noon));
    }

    [TestMethod]
    public void Remapping_an_input_moves_primary_and_secondary_assignments()
    {
        var entry = TestFactory.TextEntry();
        var oldPrimary = TopicId.New();
        var oldSecondary = TopicId.New();
        var renamed = TopicId.New();
        entry.AssignTopics(oldPrimary, [oldSecondary]);

        entry.RemapTopic(oldPrimary, renamed);
        Assert.AreEqual(renamed, entry.PrimaryTopicId);
        Assert.AreEqual(oldSecondary, entry.SecondaryTopicIds.Single());

        // Folding a secondary into the primary must collapse it, not leave it listed twice.
        entry.RemapTopic(oldSecondary, renamed);
        Assert.AreEqual(renamed, entry.PrimaryTopicId);
        Assert.AreEqual(0, entry.SecondaryTopicIds.Count);
    }

    [TestMethod]
    public void Remapping_never_leaves_a_topic_as_both_primary_and_secondary()
    {
        var entry = TestFactory.TextEntry();
        var primary = TopicId.New();
        var secondary = TopicId.New();
        entry.AssignTopics(primary, [secondary]);

        entry.RemapTopic(secondary, primary);

        Assert.AreEqual(primary, entry.PrimaryTopicId);
        Assert.AreEqual(0, entry.SecondaryTopicIds.Count);
    }

    /// <summary>
    /// The invariant test §17.1 asks for: topic surgery is invisible to historical source mappings.
    /// </summary>
    [TestMethod]
    public void Topic_surgery_never_disturbs_a_historical_source_mapping()
    {
        var input = TestFactory.TextEntry(TestFactory.Day(1), "第一段");
        var originalPrimary = TopicId.New();
        input.AssignTopics(originalPrimary, [TopicId.New()]);

        var reflection = TestFactory.NewReflection(TestFactory.Day(2));
        var version = TestFactory.NewVersion(reflection, "第一段\n\n第二段");
        var source = TestFactory.NewSource(version, 0, 0, 3, "第一段", input.Id, isHistorical: true);
        version.AttachSources([source]);

        // Rename, merge and remap — the three operations §6.2 allows.
        var target = Topic.Create(TopicId.New(), "职业", TestFactory.Noon);
        var retiring = Topic.Create(originalPrimary, "工作", TestFactory.Noon);
        retiring.MergeInto(target.Id, TestFactory.Noon);
        input.RemapTopic(originalPrimary, target.Id);

        // The source map is untouched: it cites the input, not the topic.
        var unchanged = version.Sources.Single();
        Assert.AreEqual(input.Id, unchanged.InputId);
        Assert.IsTrue(unchanged.IsHistorical);
        Assert.AreEqual(SourceDrift.Exact, version.CheckSourceDrift().Single().Drift);
    }
}
