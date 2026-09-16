using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Retrieval;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Retrieval;

/// <summary>
/// docs/开发指导.md §8.3. Retrieval is where the product's central promise is either kept or broken: the
/// material list built here is the only thing a generation run is allowed to cite, so the day boundary is
/// re-checked here even though <see cref="RecallPolicy"/> already knows it.
/// </summary>
[TestClass]
public class MaterialRetrievalTests
{
    [TestMethod]
    public void Material_from_after_the_article_day_is_dropped_however_high_it_scores()
    {
        var articleDay = TestFactory.Day(10);
        var future = TestFactory.TextEntry(TestFactory.Day(11), "明天要做的事情");
        var past = TestFactory.TextEntry(TestFactory.Day(9), "前天的事情");

        var selected = RetrievalPolicy.Select(
            [new RetrievalCandidate(future, 1.0, "相似", RetrievalSignal.Semantic)],
            [new RetrievalCandidate(past, 0.7, "共同主题", RetrievalSignal.Topic)],
            [],
            articleDay,
            RetrievalLimits.Default);

        CollectionAssert.AreEqual(new[] { past.Id }, selected.Select(m => m.Entry.Id).ToArray());
    }

    [TestMethod]
    public void An_entry_the_user_excluded_from_future_recall_is_dropped()
    {
        var articleDay = TestFactory.Day(10);
        var excluded = TestFactory.TextEntry(TestFactory.Day(9));
        excluded.SetAllowFutureRecall(false);

        var selected = RetrievalPolicy.Select(
            [new RetrievalCandidate(excluded, 1.0, "相似", RetrievalSignal.Semantic)],
            [],
            [],
            articleDay,
            RetrievalLimits.Default);

        Assert.AreEqual(0, selected.Count);
    }

    [TestMethod]
    public void A_weak_full_text_hit_is_dropped_but_the_same_score_from_a_topic_is_kept()
    {
        // Raw text overlap is noisy, so it has to clear a higher bar than an explicit topic assignment:
        // §8.3 asks for high-relevance material, not for material that shares one character.
        var articleDay = TestFactory.Day(10);
        var entry = TestFactory.TextEntry(TestFactory.Day(9));

        var viaFullText = RetrievalPolicy.Select(
            [],
            [],
            [new RetrievalCandidate(entry, 0.06, "内容相似", RetrievalSignal.FullText)],
            articleDay,
            RetrievalLimits.Default);

        var viaTopic = RetrievalPolicy.Select(
            [],
            [new RetrievalCandidate(entry, 0.06, "共同主题", RetrievalSignal.Topic)],
            [],
            articleDay,
            RetrievalLimits.Default);

        Assert.AreEqual(0, viaFullText.Count);
        Assert.AreEqual(1, viaTopic.Count);
    }

    [TestMethod]
    public void The_stronger_signal_wins_when_two_signals_find_the_same_entry()
    {
        var articleDay = TestFactory.Day(10);
        var entry = TestFactory.TextEntry(TestFactory.Day(9));

        var topicWins = RetrievalPolicy.Select(
            [new RetrievalCandidate(entry, 0.60, "语义相似", RetrievalSignal.Semantic)],
            [new RetrievalCandidate(entry, 1.00, "共同的主要主题", RetrievalSignal.Topic)],
            [],
            articleDay,
            RetrievalLimits.Default);

        var semanticWins = RetrievalPolicy.Select(
            [new RetrievalCandidate(entry, 0.90, "语义相似", RetrievalSignal.Semantic)],
            [new RetrievalCandidate(entry, 0.70, "共同的次要主题", RetrievalSignal.Topic)],
            [],
            articleDay,
            RetrievalLimits.Default);

        Assert.AreEqual(1, topicWins.Count, "One entry must never appear twice.");
        Assert.AreEqual("共同的主要主题", topicWins[0].Reason);
        Assert.AreEqual(1.0, topicWins[0].Relevance, 0.0001);

        Assert.AreEqual(1, semanticWins.Count);
        Assert.AreEqual("语义相似", semanticWins[0].Reason);
    }

    [TestMethod]
    public void At_most_the_configured_number_of_materials_is_returned()
    {
        var articleDay = TestFactory.Day(10);
        var candidates = Enumerable.Range(1, 25)
            .Select(index => new RetrievalCandidate(
                TestFactory.TextEntry(TestFactory.Day(1), $"第 {index} 条"),
                0.5,
                "共同主题",
                RetrievalSignal.Topic))
            .ToArray();

        var selected = RetrievalPolicy.Select([], candidates, [], articleDay, RetrievalLimits.Default);

        Assert.AreEqual(RetrievalLimits.Default.MaxMaterials, selected.Count);
    }

    [TestMethod]
    public void Ordering_is_by_relevance_then_by_recency()
    {
        var articleDay = TestFactory.Day(10);
        var older = TestFactory.TextEntry(TestFactory.Day(7));
        var newer = TestFactory.TextEntry(TestFactory.Day(9));
        var best = TestFactory.TextEntry(TestFactory.Day(3));

        var selected = RetrievalPolicy.Select(
            [],
            [
                new RetrievalCandidate(older, 0.7, "共同主题", RetrievalSignal.Topic),
                new RetrievalCandidate(newer, 0.7, "共同主题", RetrievalSignal.Topic),
                new RetrievalCandidate(best, 0.9, "共同主题", RetrievalSignal.Topic),
            ],
            [],
            articleDay,
            RetrievalLimits.Default);

        CollectionAssert.AreEqual(
            new[] { best.Id, newer.Id, older.Id },
            selected.Select(m => m.Entry.Id).ToArray());
    }

    [TestMethod]
    public void Equal_scores_produce_the_same_order_every_time()
    {
        var articleDay = TestFactory.Day(10);
        var entries = Enumerable.Range(0, 6)
            .Select(_ => TestFactory.TextEntry(TestFactory.Day(5)))
            .ToArray();

        var candidates = entries
            .Select(entry => new RetrievalCandidate(entry, 0.8, "共同主题", RetrievalSignal.Topic))
            .ToArray();

        var forward = RetrievalPolicy.Select(
            [],
            candidates,
            [],
            articleDay,
            RetrievalLimits.Default);

        var reversed = RetrievalPolicy.Select(
            [],
            candidates.Reverse().ToArray(),
            [],
            articleDay,
            RetrievalLimits.Default);

        CollectionAssert.AreEqual(
            forward.Select(m => m.Entry.Id).ToArray(),
            reversed.Select(m => m.Entry.Id).ToArray());
    }

    [TestMethod]
    public void Historical_material_is_flagged_but_the_article_day_itself_is_not()
    {
        var articleDay = TestFactory.Day(10);
        var sameDay = TestFactory.TextEntry(articleDay);

        var selected = RetrievalPolicy.Select(
            [],
            [new RetrievalCandidate(sameDay, 1.0, "共同主题", RetrievalSignal.Topic)],
            [],
            articleDay,
            RetrievalLimits.Default);

        Assert.IsFalse(selected[0].IsHistorical);
    }

    [TestMethod]
    public void A_shared_primary_topic_outranks_a_shared_secondary_topic()
    {
        var sharedPrimary = TestFactory.NewTopic("录音");
        var sharedSecondary = TestFactory.NewTopic("通勤");

        var today = TestFactory.FiledText(TestFactory.Day(10), "今天录音了", sharedPrimary.Id, sharedSecondary.Id);

        var primaryMatch = TestFactory.FiledText(TestFactory.Day(3), "以前的录音", sharedPrimary.Id);
        var secondaryMatch = TestFactory.FiledText(TestFactory.Day(3), "以前的通勤", null, sharedSecondary.Id);

        var candidates = RetrievalPolicy.FromTopics([today], [primaryMatch, secondaryMatch]);

        Assert.AreEqual(2, candidates.Count);
        Assert.AreEqual(1.0, candidates.Single(c => c.Entry.Id == primaryMatch.Id).Score, 0.0001);
        Assert.AreEqual(0.7, candidates.Single(c => c.Entry.Id == secondaryMatch.Id).Score, 0.0001);
    }

    [TestMethod]
    public void The_topic_signal_is_empty_when_the_day_has_no_topics_yet()
    {
        var today = TestFactory.TextEntry(TestFactory.Day(10));
        var past = TestFactory.FiledText(TestFactory.Day(3), "记录", TestFactory.NewTopic().Id);

        Assert.AreEqual(0, RetrievalPolicy.FromTopics([today], [past]).Count);
    }

    [TestMethod]
    public void The_full_text_signal_uses_the_day_text_as_the_query()
    {
        var related = TestFactory.TextEntry(TestFactory.Day(3), "上次录音上传失败");
        var unrelated = TestFactory.TextEntry(TestFactory.Day(3), "周末去爬山看日出");

        var candidates = RetrievalPolicy.FromFullText(
            "今天试了一下录音和上传",
            [related, unrelated],
            RetrievalLimits.Default.MinimumLexicalScore);

        CollectionAssert.AreEqual(new[] { related.Id }, candidates.Select(c => c.Entry.Id).ToArray());
    }

    [TestMethod]
    public void The_day_text_is_composed_in_capture_order()
    {
        var day = TestFactory.Day(10);
        var first = TestFactory.TextEntryAt(TestFactory.Utc(2026, 3, 10, 1, 0), day, "第一句");
        var second = TestFactory.TextEntryAt(TestFactory.Utc(2026, 3, 10, 9, 0), day, "第二句");

        var text = RetrievalPolicy.ComposeDayText([second, first]);

        Assert.AreEqual("第一句\n第二句", text);
    }

    [TestMethod]
    public void A_candidate_without_text_never_enters_the_material_list()
    {
        var articleDay = TestFactory.Day(10);
        var awaiting = TestFactory.VoiceEntry(TestFactory.Day(9));

        var selected = RetrievalPolicy.Select(
            [new RetrievalCandidate(awaiting, 1.0, "相似", RetrievalSignal.Semantic)],
            [],
            [],
            articleDay,
            RetrievalLimits.Default);

        Assert.AreEqual(0, selected.Count);
    }

    [TestMethod]
    public void Limits_reject_an_impossible_configuration()
    {
        TestFactory.ThrowsDomain(
            "retrieval.max_materials.out_of_range",
            () => new RetrievalLimits(0, 0.1, 0.1).Validate());

        TestFactory.ThrowsDomain(
            "retrieval.threshold.out_of_range",
            () => new RetrievalLimits(10, 1.5, 0.1).Validate());
    }
}
