using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §8.3 and §17.1: history may never be used to assert something about a day that had
/// not happened yet, and the user can switch an entry out of future recall.
/// </summary>
[TestClass]
public class RecallPolicyTests
{
    [TestMethod]
    public void Material_after_the_reflection_day_is_never_recallable()
    {
        var articleDay = TestFactory.Day(10);
        var earlier = TestFactory.TextEntry(TestFactory.Day(9));
        var sameDay = TestFactory.TextEntry(articleDay);
        var later = TestFactory.TextEntry(TestFactory.Day(11));

        var selected = RecallPolicy.SelectRecallable([earlier, sameDay, later], articleDay);

        CollectionAssert.AreEqual(
            new[] { earlier.Id, sameDay.Id },
            selected.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void An_entry_the_user_excluded_is_not_recallable()
    {
        var articleDay = TestFactory.Day(10);
        var entry = TestFactory.TextEntry(TestFactory.Day(9));
        entry.SetAllowFutureRecall(false);

        Assert.IsFalse(RecallPolicy.IsRecallable(entry, articleDay));
        Assert.AreEqual(0, RecallPolicy.SelectRecallable([entry], articleDay).Count);
    }

    [TestMethod]
    public void Deleted_entries_are_not_recallable()
    {
        var articleDay = TestFactory.Day(10);
        var entry = TestFactory.TextEntry(TestFactory.Day(9));
        entry.Delete(TestFactory.Noon);

        Assert.AreEqual(0, RecallPolicy.SelectRecallable([entry], articleDay).Count);
    }

    [TestMethod]
    public void An_entry_without_usable_text_is_not_recallable()
    {
        var articleDay = TestFactory.Day(10);
        var awaitingTranscription = TestFactory.VoiceEntry(TestFactory.Day(9));

        Assert.AreEqual(0, RecallPolicy.SelectRecallable([awaitingTranscription], articleDay).Count);
    }

    [TestMethod]
    public void Results_are_ordered_oldest_first_for_a_chronological_prompt()
    {
        var articleDay = TestFactory.Day(10);
        var day8 = TestFactory.TextEntry(TestFactory.Day(8));
        var day5 = TestFactory.TextEntry(TestFactory.Day(5));
        var day9 = TestFactory.TextEntry(TestFactory.Day(9));

        var selected = RecallPolicy.SelectRecallable([day8, day5, day9], articleDay);

        CollectionAssert.AreEqual(
            new[] { day5.Id, day8.Id, day9.Id },
            selected.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void Same_day_material_is_recallable_but_is_not_historical()
    {
        var articleDay = TestFactory.Day(10);
        var sameDay = TestFactory.TextEntry(articleDay);
        var older = TestFactory.TextEntry(TestFactory.Day(3));

        Assert.IsTrue(RecallPolicy.IsRecallable(sameDay, articleDay));
        Assert.IsFalse(RecallPolicy.IsHistorical(sameDay, articleDay));
        Assert.IsTrue(RecallPolicy.IsHistorical(older, articleDay));
    }

    /// <summary>
    /// The §7 exception remains bounded: when a late input forces a regeneration the next day, the
    /// candidate set is still cut off at the original content day.
    /// </summary>
    [TestMethod]
    public void A_regenerated_stale_day_still_cannot_see_material_from_after_it()
    {
        var staleDay = TestFactory.Day(1);
        var lateSameDayInput = TestFactory.TextEntry(staleDay);
        var nextDayInput = TestFactory.TextEntry(TestFactory.Day(2));

        var selected = RecallPolicy.SelectRecallable([lateSameDayInput, nextDayInput], staleDay);

        CollectionAssert.AreEqual(new[] { lateSameDayInput.Id }, selected.Select(e => e.Id).ToArray());
    }
}
