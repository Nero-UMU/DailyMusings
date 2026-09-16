using DailyMusings.Domain.Common;
using DailyMusings.Domain.Topics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Topics;

/// <summary>
/// docs/开发指导.md §6.2 and §8.2 step 5: inputs are filed under the topics the user already keeps.
/// Automatic recognition never invents a topic, because §6.2's value — rename, merge, re-file — only holds
/// over a vocabulary the user recognizes.
/// </summary>
[TestClass]
public class TopicMatchingTests
{
    [TestMethod]
    public void A_topic_whose_name_appears_verbatim_is_a_certain_match()
    {
        var topic = TestFactory.NewTopic("录音");

        var suggestions = TopicMatcher.Suggest("今天试了一下录音和上传，感觉还行", [topic]);

        Assert.AreEqual(1, suggestions.Count);
        Assert.AreEqual(1.0, suggestions[0].Score, 0.0001);
    }

    [TestMethod]
    public void Partial_overlap_is_filed_as_a_secondary_topic_not_as_the_primary()
    {
        // 录音 and 上传 both appear, but the topic name as a whole does not, so this is a real-but-weaker
        // signal — exactly what a secondary topic is for (§6.2).
        var topic = TestFactory.NewTopic("录音上传");

        var suggestions = TopicMatcher.Suggest("今天录音完就上传了，很顺利", [topic]);
        var (primary, secondary) = TopicMatcher.ToAssignment(suggestions);

        Assert.AreEqual(1, suggestions.Count);
        Assert.IsTrue(suggestions[0].Score >= TopicMatcher.MinimumSuggestionScore);
        Assert.IsTrue(suggestions[0].Score < TopicMatcher.PrimaryThreshold);
        Assert.IsNull(primary);
        CollectionAssert.AreEqual(new[] { topic.Id }, secondary.ToArray());
    }

    [TestMethod]
    public void A_strong_match_becomes_primary_and_weaker_ones_become_secondary()
    {
        var strong = TestFactory.NewTopic("录音");
        var weak = TestFactory.NewTopic("录音上传");

        var suggestions = TopicMatcher.Suggest("今天试了一下录音上传", [strong, weak]);

        Assert.AreEqual(2, suggestions.Count);
        Assert.AreEqual(strong.Id, suggestions[0].TopicId, "The verbatim match must rank first.");

        var (primary, secondary) = TopicMatcher.ToAssignment(suggestions);
        Assert.AreEqual(strong.Id, primary);
        CollectionAssert.AreEqual(new[] { weak.Id }, secondary.ToArray());
    }

    [TestMethod]
    public void An_unrelated_topic_is_not_suggested()
    {
        var topic = TestFactory.NewTopic("爬山");

        Assert.AreEqual(0, TopicMatcher.Suggest("今天试了一下录音和上传", [topic]).Count);
    }

    [TestMethod]
    public void A_merged_topic_never_attracts_new_assignments()
    {
        // A merge leaves a tombstone (decision A.9). Re-filing new material under a retired topic would
        // resurrect it in every list the user sees, so it is excluded at the source.
        var retired = TestFactory.NewTopic("旧主题");
        var target = TestFactory.NewTopic("新主题");
        retired.MergeInto(target.Id, TestFactory.Noon);

        Assert.AreEqual(0, TopicMatcher.Suggest("今天聊了旧主题", [retired]).Count);
    }

    [TestMethod]
    public void At_most_two_secondary_topics_are_attached()
    {
        var suggestions = new[]
        {
            new TopicSuggestion(TopicId.New(), "一", 1.0),
            new TopicSuggestion(TopicId.New(), "二", 0.7),
            new TopicSuggestion(TopicId.New(), "三", 0.7),
            new TopicSuggestion(TopicId.New(), "四", 0.7),
        };

        var (primary, secondary) = TopicMatcher.ToAssignment(suggestions);

        Assert.AreEqual(suggestions[0].TopicId, primary);
        Assert.AreEqual(TopicMatcher.MaxSecondaryTopics, secondary.Count);
        Assert.IsFalse(secondary.Contains(primary!.Value), "The primary must never also be a secondary (§6.2).");
    }

    [TestMethod]
    public void A_later_strong_match_does_not_displace_the_first_primary()
    {
        var suggestions = new[]
        {
            new TopicSuggestion(TopicId.New(), "甲", 0.9),
            new TopicSuggestion(TopicId.New(), "乙", 0.8),
            new TopicSuggestion(TopicId.New(), "丙", 0.8),
            new TopicSuggestion(TopicId.New(), "丁", 0.8),
        };

        var (primary, secondary) = TopicMatcher.ToAssignment(suggestions);

        Assert.AreEqual(suggestions[0].TopicId, primary);
        Assert.AreEqual(2, secondary.Count);
        CollectionAssert.DoesNotContain(secondary.ToArray(), primary!.Value);
    }

    [TestMethod]
    public void Empty_text_or_an_empty_topic_set_suggests_nothing()
    {
        Assert.AreEqual(0, TopicMatcher.Suggest("   ", [TestFactory.NewTopic()]).Count);
        Assert.AreEqual(0, TopicMatcher.Suggest("今天录音了", []).Count);
    }

    [TestMethod]
    public void Normalisation_ignores_case_spacing_and_punctuation()
    {
        Assert.AreEqual(TopicMatcher.Normalize("录音 上传"), TopicMatcher.Normalize("录音、上传"));
        Assert.AreEqual(TopicMatcher.Normalize("Hexo"), TopicMatcher.Normalize("hexo"));
    }
}
