using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §7 and §17.1. These predicates decide whether a day gets written at all, so they are pure
/// functions the tests can hit directly rather than behaviour observed through a running scheduler.
/// </summary>
[TestClass]
public class GenerationRulesTests
{
    private static readonly TimeOnly GenerationTime = new(23, 0);

    [TestMethod]
    public void An_arbitrary_past_day_cannot_be_generated_on_request()
    {
        // §7: 不支持选择任意历史日期重新生成. Re-running a closed day would silently rewrite history; the
        // catch-up scan is the only path that touches past days, and it records a different reason.
        var decision = GenerationRules.ForManualRequest(
            requested: TestFactory.Day(9),
            currentContentDay: TestFactory.Day(10),
            status: null,
            hasMaterial: true,
            hasBlockingFailures: false);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("reflection.regeneration.date_not_current", decision.Code);
    }

    [TestMethod]
    public void A_future_day_cannot_be_generated_either()
    {
        var decision = GenerationRules.ForManualRequest(
            requested: TestFactory.Day(11),
            currentContentDay: TestFactory.Day(10),
            status: null,
            hasMaterial: true,
            hasBlockingFailures: false);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("reflection.regeneration.date_not_current", decision.Code);
    }

    [TestMethod]
    public void The_current_day_with_material_may_be_generated()
    {
        var decision = GenerationRules.ForManualRequest(
            requested: TestFactory.Day(10),
            currentContentDay: TestFactory.Day(10),
            status: null,
            hasMaterial: true,
            hasBlockingFailures: false);

        Assert.IsTrue(decision.Allowed, decision.Detail);
        decision.EnsureAllowed();
    }

    [TestMethod]
    public void An_explicit_request_may_retry_what_the_nightly_scan_refuses_to()
    {
        // §14: 用户修复配置后可以重试单项或批量补跑失败任务. The scheduled run must not keep retrying a
        // terminally failed draft, but the user asking for it is a fresh decision.
        var manual = GenerationRules.ForManualRequest(
            requested: TestFactory.Day(10),
            currentContentDay: TestFactory.Day(10),
            status: ReflectionStatus.Failed,
            hasMaterial: true,
            hasBlockingFailures: false);

        Assert.IsTrue(manual.Allowed, manual.Detail);

        Assert.IsFalse(GenerationRules.ForScheduledRun(ReflectionStatus.Failed, true, false).Allowed);
    }

    [TestMethod]
    public void An_explicit_request_still_respects_the_day_boundary_and_the_input_rules()
    {
        Assert.AreEqual(
            "reflection.generation.no_inputs",
            GenerationRules.ForManualRequest(
                TestFactory.Day(10),
                TestFactory.Day(10),
                null,
                hasMaterial: false,
                hasBlockingFailures: false).Code);

        Assert.AreEqual(
            "reflection.generation.transcription_failures",
            GenerationRules.ForManualRequest(
                TestFactory.Day(10),
                TestFactory.Day(10),
                null,
                hasMaterial: true,
                hasBlockingFailures: true).Code);
    }

    [TestMethod]
    public void A_day_without_input_never_produces_an_empty_article()
    {
        var decision = GenerationRules.ForScheduledRun(null, hasMaterial: false, hasBlockingFailures: false);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("reflection.generation.no_inputs", decision.Code);
    }

    [TestMethod]
    public void A_failed_transcription_defers_generation_until_the_user_ignores_it()
    {
        var deferred = GenerationRules.ForScheduledRun(null, hasMaterial: true, hasBlockingFailures: true);

        Assert.IsFalse(deferred.Allowed);
        Assert.AreEqual("reflection.generation.transcription_failures", deferred.Code);

        // §7: 用户可选择忽略失败项继续 — the caller passes false once the user has agreed to go ahead.
        Assert.IsTrue(GenerationRules.ForScheduledRun(null, hasMaterial: true, hasBlockingFailures: false).Allowed);
    }

    [TestMethod]
    public void Each_draft_status_states_its_own_reason_for_not_generating()
    {
        Assert.AreEqual(
            "reflection.generation.in_progress",
            GenerationRules.ForScheduledRun(ReflectionStatus.Generating, true, false).Code);

        Assert.AreEqual(
            "reflection.generation.awaiting_review",
            GenerationRules.ForScheduledRun(ReflectionStatus.ReviewRequired, true, false).Code);

        Assert.AreEqual(
            "reflection.generation.already_confirmed",
            GenerationRules.ForScheduledRun(ReflectionStatus.Confirmed, true, false).Code);

        // A terminally failed draft waits for a human (§14: fix the cause, then retry) instead of burning
        // another automatic attempt.
        Assert.AreEqual(
            "reflection.generation.previously_failed",
            GenerationRules.ForScheduledRun(ReflectionStatus.Failed, true, false).Code);
    }

    [TestMethod]
    public void A_stale_draft_is_regenerated_and_a_pending_one_is_started()
    {
        Assert.IsTrue(GenerationRules.ForScheduledRun(ReflectionStatus.StaleByLateInput, true, false).Allowed);
        Assert.IsTrue(GenerationRules.ForScheduledRun(ReflectionStatus.PendingInputs, true, false).Allowed);
        Assert.IsTrue(GenerationRules.ForScheduledRun(ReflectionStatus.Ready, true, false).Allowed);
    }

    [TestMethod]
    public void The_slot_is_due_only_after_the_configured_local_time()
    {
        var calendar = TestFactory.Shanghai();

        // 23:00 in Asia/Shanghai is 15:00 UTC the same day.
        var justBefore = TestFactory.Utc(2026, 3, 10, 14, 59, 59);
        var atSlot = TestFactory.Utc(2026, 3, 10, 15, 0, 0);

        Assert.IsFalse(GenerationRules.IsSlotDue(TestFactory.Day(10), justBefore, calendar, GenerationTime));
        Assert.IsTrue(GenerationRules.IsSlotDue(TestFactory.Day(10), atSlot, calendar, GenerationTime));

        // A past day is always due, which is what lets one predicate serve both the nightly run and the
        // catch-up scan after downtime.
        Assert.IsTrue(GenerationRules.IsSlotDue(TestFactory.Day(9), justBefore, calendar, GenerationTime));
    }

    [TestMethod]
    public void Material_arriving_after_the_draft_was_produced_makes_it_stale()
    {
        // §7's two paths — same-day input after the nightly run, and yesterday's input arriving today —
        // both land on the draft named by the input's own content day.
        Assert.IsTrue(GenerationRules.ShouldMarkStale(ReflectionStatus.ReviewRequired));
        Assert.IsTrue(GenerationRules.ShouldMarkStale(ReflectionStatus.Confirmed));
    }

    [TestMethod]
    public void Statuses_that_cannot_go_stale_are_not_marked_stale()
    {
        // Read off the transition table rather than a second hand-written list (A.3): a draft that has not
        // been produced yet has nothing to invalidate, and one already stale needs no second mark.
        foreach (var status in new[]
                 {
                     ReflectionStatus.PendingInputs,
                     ReflectionStatus.Ready,
                     ReflectionStatus.Generating,
                     ReflectionStatus.Failed,
                     ReflectionStatus.StaleByLateInput,
                 })
        {
            Assert.IsFalse(GenerationRules.ShouldMarkStale(status), $"{status} must not be marked stale.");
        }
    }

    [TestMethod]
    public void A_failed_transcription_blocks_the_day_but_a_deleted_one_does_not()
    {
        var failed = TestFactory.VoiceEntry(TestFactory.Day(10));
        failed.BeginTranscription();
        failed.FailTranscription("transcription.timeout");

        Assert.IsTrue(GenerationRules.HasBlockingTranscriptionFailures([failed]));

        failed.Delete(TestFactory.Noon);
        Assert.IsFalse(
            GenerationRules.HasBlockingTranscriptionFailures([failed]),
            "A deleted entry is not part of the material set at all.");

        var text = TestFactory.TextEntry(TestFactory.Day(10));
        Assert.IsFalse(
            GenerationRules.HasBlockingTranscriptionFailures([text]),
            "A text entry has nothing to transcribe, so it can never be a failure.");
    }

    [TestMethod]
    public void The_material_set_skips_deleted_and_empty_entries_and_reads_in_time_order()
    {
        var day = TestFactory.Day(10);
        var later = TestFactory.TextEntryAt(TestFactory.Utc(2026, 3, 10, 9, 0), day, "后来的");
        var earlier = TestFactory.TextEntryAt(TestFactory.Utc(2026, 3, 10, 1, 0), day, "先说的");
        var awaiting = TestFactory.VoiceEntry(day);
        var deleted = TestFactory.TextEntryAt(TestFactory.Utc(2026, 3, 10, 2, 0), day, "删掉的");
        deleted.Delete(TestFactory.Noon);

        var material = GenerationRules.SelectDayMaterial([later, awaiting, deleted, earlier]);

        CollectionAssert.AreEqual(new[] { earlier.Id, later.Id }, material.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void Two_captures_at_the_same_instant_always_produce_the_same_prompt_order()
    {
        var day = TestFactory.Day(10);
        var first = TestFactory.TextEntryAt(TestFactory.Noon, day, "甲");
        var second = TestFactory.TextEntryAt(TestFactory.Noon, day, "乙");

        var forward = GenerationRules.SelectDayMaterial([first, second]);
        var reversed = GenerationRules.SelectDayMaterial([second, first]);

        CollectionAssert.AreEqual(
            forward.Select(e => e.Id).ToArray(),
            reversed.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void A_late_arrival_is_judged_only_by_the_input_itself()
    {
        // A.4: "whether this is late" must not depend on whether the generation job has run, which is why
        // it lives on the calendar and takes no draft into account.
        var calendar = TestFactory.Shanghai();

        var beforeDayEnds = TestFactory.Utc(2026, 3, 10, 15, 59, 0); // 23:59 Shanghai on the 10th
        var afterDayEnds = TestFactory.Utc(2026, 3, 10, 16, 0, 0); // 00:00 Shanghai on the 11th

        Assert.IsFalse(calendar.IsLateArrival(TestFactory.Day(10), beforeDayEnds));
        Assert.IsTrue(calendar.IsLateArrival(TestFactory.Day(10), afterDayEnds));
    }

    [TestMethod]
    public void A_stale_draft_of_an_earlier_day_may_only_cite_that_day_and_before()
    {
        // §7's single exception: a stale draft processed the next day still uses the material available on
        // its own content day. The boundary is enforced by the article date passed to retrieval, never by
        // "today".
        var staleDay = TestFactory.Day(10);
        var theDayItself = TestFactory.TextEntry(staleDay);
        var theDayAfter = TestFactory.TextEntry(TestFactory.Day(11));

        var selected = RecallPolicy.SelectRecallable([theDayItself, theDayAfter], staleDay);

        CollectionAssert.AreEqual(new[] { theDayItself.Id }, selected.Select(e => e.Id).ToArray());
    }
}
