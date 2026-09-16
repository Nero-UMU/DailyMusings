using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Publishing;

/// <summary>
/// docs/开发指导.md §11.1 and decision A.2: what an unattended run is allowed to do, and when it must stop
/// instead. This is the rule that keeps the product from publishing anything to the public internet that nobody
/// asked it to publish, so it is asserted directly rather than through a job.
/// </summary>
[TestClass]
public class PublishPlanningTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(120);

    [TestMethod]
    public void An_unattended_run_that_was_never_opted_in_uploads_a_draft()
    {
        var target = TestFactory.NewTarget(automatic: false);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Public);

        var intent = PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon);

        Assert.AreEqual(PublicationIntent.UploadDraft, intent);
    }

    [TestMethod]
    public void An_unattended_run_publishes_publicly_once_the_target_opted_in()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Public);

        var intent = PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon);

        Assert.AreEqual(PublicationIntent.PublishPublicly, intent);
    }

    [TestMethod]
    public void An_automatic_slot_that_only_asked_for_a_draft_stays_a_draft_even_when_opted_in()
    {
        // The opt-in permits unattended publicity; it does not demand it. A night that was scheduled to produce
        // a draft for review must not be turned into a public post by a switch set months earlier.
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Draft);

        Assert.AreEqual(
            PublicationIntent.UploadDraft,
            PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon));
    }

    [TestMethod]
    public void A_human_request_decides_for_itself()
    {
        var target = TestFactory.NewTarget(automatic: false);
        var now = TestFactory.Noon;

        var wantsPublic = TestFactory.NewPublication(
            target,
            PublicationTrigger.Manual,
            now,
            PublicationVisibility.Public);

        var wantsDraft = TestFactory.NewPublication(
            target,
            PublicationTrigger.Manual,
            now,
            PublicationVisibility.Draft);

        // §11.1: a device token may manually publish, and the opt-in gate is about *unattended* runs only.
        Assert.AreEqual(PublicationIntent.PublishPublicly, PublicationPlanner.Decide(wantsPublic, target, Window, now));
        Assert.AreEqual(PublicationIntent.UploadDraft, PublicationPlanner.Decide(wantsDraft, target, Window, now));
    }

    [TestMethod]
    public void A_window_that_elapsed_expires_instead_of_running()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var scheduled = TestFactory.Noon;
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            scheduled,
            PublicationVisibility.Public);

        var insideWindow = scheduled + Window - TimeSpan.FromSeconds(1);
        var outsideWindow = scheduled + Window + TimeSpan.FromSeconds(1);

        Assert.AreEqual(
            PublicationIntent.PublishPublicly,
            PublicationPlanner.Decide(publication, target, Window, insideWindow));

        // §14: 超窗…不得静默补发. The record is expired rather than run late, and no automatic action follows.
        Assert.AreEqual(
            PublicationIntent.Expire,
            PublicationPlanner.Decide(publication, target, Window, outsideWindow));
    }

    [TestMethod]
    public void Reaching_the_remote_late_is_still_too_late()
    {
        // The window protects against the delay, not against the visibility: an unattended run that only wanted
        // to upload a draft must also stop, or the instance would quietly push stale material hours after the
        // user stopped expecting it.
        var target = TestFactory.NewTarget(automatic: false);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Draft);

        Assert.AreEqual(
            PublicationIntent.Expire,
            PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon + Window + TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public void A_publication_that_is_not_queued_is_left_alone()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Public);

        publication.Begin("system:scheduler", TestFactory.Noon);

        Assert.AreEqual(
            PublicationIntent.Skip,
            PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon),
            "A run that is on the network must not be started again.");

        publication.CompleteAsPublished("42", "hash", TestFactory.Noon);

        Assert.AreEqual(
            PublicationIntent.Skip,
            PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon),
            "A finished publication is never run again.");
    }

    [TestMethod]
    public void A_failed_attempt_may_be_re_entered_by_the_jobs_own_retry()
    {
        var target = TestFactory.NewTarget(automatic: true);
        var publication = TestFactory.NewPublication(
            target,
            PublicationTrigger.Automatic,
            TestFactory.Noon,
            PublicationVisibility.Public);

        publication.Begin("system:scheduler", TestFactory.Noon);
        publication.Fail("publication.network", "offline", TestFactory.Noon, RetryPolicy.Default);

        // §14's bounded retry continues the same attempt, so the planner must let it through — while a Terminated
        // record is still never touched by the scheduled path.
        Assert.AreEqual(
            PublicationIntent.PublishPublicly,
            PublicationPlanner.Decide(publication, target, Window, TestFactory.Noon));
    }

    [TestMethod]
    public void Only_an_unstarted_or_draft_stage_publication_is_superseded_by_new_material()
    {
        // §11.1: 夜间新增素材使待发布版本失效. A request already in flight is left alone — it is on the network,
        // and rewriting the local record would only make it disagree with the remote.
        Assert.IsTrue(PublicationSupersession.ShouldSupersede(PublicationStatus.Queued));
        Assert.IsTrue(PublicationSupersession.ShouldSupersede(PublicationStatus.DraftUploaded));
        Assert.IsFalse(PublicationSupersession.ShouldSupersede(PublicationStatus.InProgress));
        Assert.IsFalse(PublicationSupersession.ShouldSupersede(PublicationStatus.Published));
        Assert.IsFalse(PublicationSupersession.ShouldSupersede(PublicationStatus.Failed));
        Assert.IsFalse(PublicationSupersession.ShouldSupersede(PublicationStatus.Expired));
        Assert.IsFalse(PublicationSupersession.ShouldSupersede(PublicationStatus.Superseded));
    }

    [TestMethod]
    public void Publishing_records_what_was_sent_and_reads_it_back_as_in_sync()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target);

        publication.Begin("owner", TestFactory.Noon);
        publication.CompleteAsDraft("17", "hash-of-what-we-sent", TestFactory.Noon);

        var comparison = publication.CompareWithRemote("hash-of-what-we-sent");

        Assert.IsTrue(comparison.RemoteChecked);
        Assert.IsTrue(comparison.InSync);
        Assert.AreEqual("17", publication.RemoteId);
    }

    [TestMethod]
    public void A_changed_draft_and_a_changed_remote_article_are_reported_separately()
    {
        var target = TestFactory.NewTarget();
        var publication = TestFactory.NewPublication(target);
        publication.Begin("owner", TestFactory.Noon);
        publication.CompleteAsDraft("17", "sent", TestFactory.Noon);

        var localOnly = publication.CompareWithRemote("edited-locally");
        Assert.IsTrue(localOnly.OnlyLocalChanged);
        Assert.IsFalse(localOnly.RemoteChanged);

        publication.RecordRemoteObservation("edited-on-the-site", TestFactory.Noon);
        var remoteOnly = publication.CompareWithRemote("sent");
        Assert.IsTrue(remoteOnly.OnlyRemoteChanged);

        var both = publication.CompareWithRemote("edited-locally");
        Assert.IsTrue(both.BothChanged);
        Assert.IsFalse(both.InSync);
    }

    [TestMethod]
    public void A_remote_that_was_never_read_is_reported_as_unknown_rather_than_in_sync()
    {
        var publication = TestFactory.NewPublication(TestFactory.NewTarget());

        var comparison = publication.CompareWithRemote("anything");

        // "In sync" and "nobody has looked" must never be the same answer: the first invites a silent overwrite.
        Assert.IsFalse(comparison.RemoteChecked);
        Assert.IsFalse(comparison.InSync);
    }
}
