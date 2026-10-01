using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §6.3, §7 and decision A.3. Two product promises live here: a confirmed draft is
/// never silently invalidated by anything except new same-day material, and there is no path that
/// regenerates an arbitrary past day.
/// </summary>
[TestClass]
public class ReflectionLifecycleTests
{
    private static Reflection Ready()
    {
        var reflection = TestFactory.NewReflection();
        reflection.MarkReady(TestFactory.Noon);
        return reflection;
    }

    private static Reflection InReview()
    {
        var reflection = Ready();
        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
        var version = TestFactory.NewVersion(reflection);
        reflection.ApplyGeneratedVersion(version.Id, false, false, TestFactory.Noon);
        return reflection;
    }

    private static Reflection Confirmed()
    {
        var reflection = InReview();
        reflection.Confirm(reflection.WorkingVersionId!.Value, TestFactory.Noon);
        return reflection;
    }

    [TestMethod]
    public void A_new_day_starts_waiting_for_inputs()
    {
        var reflection = TestFactory.NewReflection();

        Assert.AreEqual(ReflectionStatus.PendingInputs, reflection.Status);
        Assert.IsNull(reflection.InitialVersionId);
        Assert.IsNull(reflection.WorkingVersionId);
    }

    [TestMethod]
    public void Generation_requires_a_ready_day()
    {
        var reflection = TestFactory.NewReflection();

        TestFactory.ThrowsDomain(
            "reflection.status.illegal_transition",
            () => reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon));
    }

    [TestMethod]
    public void Generated_content_lands_in_review_and_opens_the_first_version_slot()
    {
        var reflection = Ready();
        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
        var version = TestFactory.NewVersion(reflection);

        reflection.ApplyGeneratedVersion(version.Id, false, false, TestFactory.Noon);

        Assert.AreEqual(ReflectionStatus.ReviewRequired, reflection.Status);
        Assert.AreEqual(version.Id, reflection.InitialVersionId);
        Assert.AreEqual(version.Id, reflection.WorkingVersionId);
        Assert.IsNull(reflection.PreviousVersionId);
    }

    [TestMethod]
    public void A_failed_generation_can_be_retried_or_sent_back_to_ready()
    {
        var reflection = Ready();
        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);

        reflection.MarkGenerationFailed(TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.Failed, reflection.Status);

        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.Generating, reflection.Status);

        reflection.MarkGenerationFailed(TestFactory.Noon);
        reflection.MarkReadyForRetry(TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.Ready, reflection.Status);
    }

    [TestMethod]
    public void Only_the_working_version_can_be_confirmed()
    {
        var reflection = InReview();
        var other = TestFactory.NewVersion(reflection);

        TestFactory.ThrowsDomain("reflection.confirm.not_working_version", () => reflection.Confirm(other.Id, TestFactory.Noon));

        reflection.Confirm(reflection.WorkingVersionId!.Value, TestFactory.Noon);

        Assert.AreEqual(ReflectionStatus.Confirmed, reflection.Status);
        Assert.AreEqual(reflection.WorkingVersionId, reflection.ConfirmedVersionId);
    }

    /// <summary>§7: 当日生成后又出现当日输入，草稿标记为过期并补生成.</summary>
    [TestMethod]
    public void Late_same_day_input_marks_a_draft_stale()
    {
        var reflection = InReview();

        reflection.MarkStaleByLateInput(TestFactory.Noon);

        Assert.AreEqual(ReflectionStatus.StaleByLateInput, reflection.Status);
        Assert.AreEqual(StaleReason.LateInput, reflection.LastStaleReason);

        // Regeneration is reachable from a stale draft — that is the §7 exception, and nothing broader.
        CollectionAssert.Contains(
            ReflectionStatusTransitions.From(ReflectionStatus.StaleByLateInput).ToArray(),
            ReflectionStatus.Generating);
    }

    [TestMethod]
    public void A_confirmed_draft_leaves_confirmed_for_late_input_or_when_the_user_asks_again()
    {
        // 两条出边：新的当日素材让它失效，或者**用户自己**要求重新生成（A.21 / A.40）。
        // 挡住自动运行的从来不是这张表，而是 ForScheduledRun——它至今仍然拒绝已确认的稿件。
        var exits = ReflectionStatusTransitions.From(ReflectionStatus.Confirmed);
        CollectionAssert.AreEqual(
            new[] { ReflectionStatus.StaleByLateInput, ReflectionStatus.Generating },
            exits.ToArray());

        var asked = Confirmed();
        asked.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.Generating, asked.Status);
        Assert.AreEqual(GenerationReason.Manual, asked.GenerationReason);

        var wentStale = Confirmed();
        wentStale.MarkStaleByLateInput(TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.StaleByLateInput, wentStale.Status);

        wentStale.BeginGeneration(GenerationReason.LateInputRegeneration, TestFactory.Noon);
        Assert.AreEqual(ReflectionStatus.Generating, wentStale.Status);
        Assert.AreEqual(GenerationReason.LateInputRegeneration, wentStale.GenerationReason);
    }

    [TestMethod]
    public void A_stale_draft_may_be_accepted_without_regenerating()
    {
        var reflection = InReview();
        var confirmed = reflection.WorkingVersionId!.Value;
        reflection.Confirm(confirmed, TestFactory.Noon);
        reflection.MarkStaleByLateInput(TestFactory.Noon);

        reflection.Confirm(confirmed, TestFactory.Noon);

        Assert.AreEqual(ReflectionStatus.Confirmed, reflection.Status);
        // The audit trail survives the round trip even though the status went back to Confirmed.
        Assert.AreEqual(StaleReason.LateInput, reflection.LastStaleReason);
    }

    [TestMethod]
    public void The_audit_reason_is_cleared_once_a_regeneration_succeeds()
    {
        var reflection = InReview();
        reflection.MarkStaleByLateInput(TestFactory.Noon);
        reflection.BeginGeneration(GenerationReason.LateInputRegeneration, TestFactory.Noon);
        var replacement = TestFactory.NewVersion(reflection);

        reflection.ApplyGeneratedVersion(replacement.Id, false, false, TestFactory.Noon);

        Assert.IsNull(reflection.LastStaleReason);
        Assert.AreEqual(ReflectionStatus.ReviewRequired, reflection.Status);
    }

    [TestMethod]
    public void Every_status_in_the_diagram_has_documented_outgoing_transitions()
    {
        // Guards against someone "simplifying" the enum back to a handful of values plus flags (A.3).
        foreach (var status in Enum.GetValues<ReflectionStatus>())
        {
            Assert.IsTrue(
                ReflectionStatusTransitions.From(status).Count > 0,
                $"{status} should have documented outgoing transitions.");
        }

        Assert.AreEqual(7, Enum.GetValues<ReflectionStatus>().Length);
    }
}
