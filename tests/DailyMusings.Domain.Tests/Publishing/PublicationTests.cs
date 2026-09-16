using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Publishing;

/// <summary>
/// docs/开发指导.md §11.1, §14, §17.1 and decisions A.2/A.7. The behaviour under test is the product's
/// safety promise: nothing becomes public unattended unless a human opted in <em>and</em> the scheduled
/// slot has not slipped.
/// </summary>
[TestClass]
public class PublicationTests
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(120);
    private static readonly RetryPolicy Policy = RetryPolicy.Default;

    [TestMethod]
    public void Unattended_publishing_is_blocked_unless_the_target_opted_in()
    {
        var target = TestFactory.NewTarget(automatic: false);
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Automatic);

        var gate = publication.CanRunAutomatically(target, DefaultWindow, TestFactory.Noon);

        Assert.IsFalse(gate.Allowed);
        Assert.AreEqual(AutomaticPublishBlock.TargetNotOptedIn, gate.Reason);
    }

    [TestMethod]
    public void Opting_in_is_attributable_to_an_administrator()
    {
        var target = TestFactory.NewTarget();

        Assert.IsFalse(target.AutomaticPublishEnabled);

        target.EnableAutomaticPublish("admin", TestFactory.Noon);

        Assert.IsTrue(target.AutomaticPublishEnabled);
        Assert.AreEqual("admin", target.AutomaticPublishEnabledBy);
        Assert.AreEqual(TestFactory.Noon, target.AutomaticPublishEnabledAtUtc);
    }

    [TestMethod]
    public void Opting_in_without_naming_an_administrator_is_refused()
    {
        var target = TestFactory.NewTarget();

        Assert.ThrowsException<ArgumentException>(() => target.EnableAutomaticPublish("   ", TestFactory.Noon));
        Assert.IsFalse(target.AutomaticPublishEnabled);
    }

    [TestMethod]
    public void A_slot_that_slipped_past_the_window_is_never_published_automatically()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var scheduled = TestFactory.Utc(2026, 3, 2, 0, 0);
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Automatic, scheduled);

        var justInside = scheduled + DefaultWindow;
        var justOutside = justInside.AddTicks(1);

        Assert.IsTrue(publication.CanRunAutomatically(target, DefaultWindow, justInside).Allowed);
        Assert.AreEqual(
            AutomaticPublishBlock.WindowExpired,
            publication.CanRunAutomatically(target, DefaultWindow, justOutside).Reason);
    }

    [TestMethod]
    public void A_manual_request_does_not_go_through_the_automatic_gate()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Manual);

        Assert.AreEqual(
            AutomaticPublishBlock.NotAutomaticRequest,
            publication.CanRunAutomatically(target, DefaultWindow, TestFactory.Noon).Reason);
    }

    [TestMethod]
    public void The_gate_stops_applying_once_the_run_has_started()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Automatic);
        publication.Begin("system:scheduler", TestFactory.Noon);

        Assert.AreEqual(
            AutomaticPublishBlock.NotQueued,
            publication.CanRunAutomatically(target, DefaultWindow, TestFactory.Noon).Reason);
    }

    [TestMethod]
    public void The_default_path_uploads_a_draft_and_can_then_be_promoted()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Manual);

        publication.Begin("pixel-8", TestFactory.Noon);
        Assert.AreEqual(PublicationStatus.InProgress, publication.Status);
        Assert.AreEqual(1, publication.AttemptCount);
        Assert.AreEqual("pixel-8", publication.TriggeredBy);

        publication.CompleteAsDraft("wp-42", TestFactory.Noon);
        Assert.AreEqual(PublicationStatus.DraftUploaded, publication.Status);
        Assert.AreEqual("wp-42", publication.RemoteId);

        publication.PromoteDraftToPublished(TestFactory.Noon);
        Assert.AreEqual(PublicationStatus.Published, publication.Status);
    }

    [TestMethod]
    public void A_manual_run_must_record_who_triggered_it()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Manual);

        TestFactory.ThrowsDomain("publication.manual.actor_required", () => publication.Begin("  ", TestFactory.Noon));

        // Rejected before any state change, so a retry with a proper actor still starts cleanly.
        Assert.AreEqual(PublicationStatus.Queued, publication.Status);
        Assert.AreEqual(0, publication.AttemptCount);
    }

    [TestMethod]
    public void A_failed_run_is_recorded_and_can_be_retried_by_a_human()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Automatic);
        publication.Begin("system:scheduler", TestFactory.Noon);

        publication.Fail("wordpress.unreachable", "connection refused", TestFactory.Noon, Policy);
        Assert.AreEqual(PublicationStatus.Failed, publication.Status);
        Assert.AreEqual("wordpress.unreachable", publication.ErrorCode);

        publication.RequeueForManualRetry("admin", TestFactory.Noon);
        Assert.AreEqual(PublicationStatus.Queued, publication.Status);
        Assert.AreEqual(0, publication.AttemptCount);
        Assert.IsNull(publication.ErrorCode);
    }

    [TestMethod]
    public void An_expired_slot_comes_back_only_through_an_explicit_retry()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Automatic);

        publication.Expire(TestFactory.Noon.AddHours(5));
        Assert.AreEqual(PublicationStatus.Expired, publication.Status);

        // Even with the target opted in and attempts remaining, an expired publication stays put.
        Assert.AreEqual(
            AutomaticPublishBlock.NotQueued,
            publication.CanRunAutomatically(target, DefaultWindow, TestFactory.Noon.AddHours(5)).Reason);

        publication.RequeueForManualRetry("admin", TestFactory.Noon.AddHours(6));
        Assert.AreEqual(PublicationStatus.Queued, publication.Status);
    }

    [TestMethod]
    public void New_night_material_supersedes_an_attempt_that_has_not_gone_out()
    {
        var target = TestFactory.NewTarget();
        var queued = TestFactory.NewPublication(target, PublicationTrigger.Automatic);
        var uploaded = TestFactory.NewPublication(target, PublicationTrigger.Automatic);

        uploaded.Begin("system:scheduler", TestFactory.Noon);
        uploaded.CompleteAsDraft("wp-7", TestFactory.Noon);

        queued.Supersede(TestFactory.Noon);
        uploaded.Supersede(TestFactory.Noon);

        Assert.AreEqual(PublicationStatus.Superseded, queued.Status);
        Assert.AreEqual(PublicationStatus.Superseded, uploaded.Status);
    }

    [TestMethod]
    public void A_published_article_is_terminal()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Manual);
        publication.Begin("admin", TestFactory.Noon);
        publication.CompleteAsPublished("wp-1", TestFactory.Noon);

        Assert.IsTrue(publication.IsTerminal);
        TestFactory.ThrowsDomain(
            "publication.status.illegal_transition",
            () => publication.RequeueForManualRetry("admin", TestFactory.Noon));
    }

    [TestMethod]
    public void A_remote_id_is_required_before_a_run_can_be_recorded_as_uploaded()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target, PublicationTrigger.Manual);
        publication.Begin("admin", TestFactory.Noon);

        Assert.ThrowsException<ArgumentException>(() => publication.CompleteAsDraft("   ", TestFactory.Noon));
        Assert.AreEqual(PublicationStatus.InProgress, publication.Status);
    }

    [TestMethod]
    public void The_window_must_be_positive_and_the_deadline_is_inclusive()
    {
        TestFactory.ThrowsDomain("publish.window.invalid", () => PublishWindow.Deadline(TestFactory.Noon, TimeSpan.Zero));

        var deadline = PublishWindow.Deadline(TestFactory.Noon, DefaultWindow);

        Assert.AreEqual(TestFactory.Noon + DefaultWindow, deadline);
        Assert.IsFalse(PublishWindow.IsExpired(TestFactory.Noon, DefaultWindow, deadline));
        Assert.IsTrue(PublishWindow.IsExpired(TestFactory.Noon, DefaultWindow, deadline.AddTicks(1)));
    }
}
