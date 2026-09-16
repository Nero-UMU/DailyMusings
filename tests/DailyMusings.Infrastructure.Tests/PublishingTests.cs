using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Notifications;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Notifications;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// Publishing and notifications over real storage (docs/开发指导.md §11, §12, §17.2).
/// <para>
/// These are the checks that decide whether something reaches the public internet, so they are asserted end to end
/// rather than through a mock of our own interface: the remote is a double, but the queue, the records, the
/// markdown directory and the settings are the real ones.
/// </para>
/// </summary>
[TestClass]
public class PublishingTests
{
    private static ContentDate Day(PublishingTestContext context) => ContentDate.From(context.Today);

    [TestMethod]
    public async Task Only_a_confirmed_draft_can_be_published()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");

        var (reflection, _) = await context.SeedConfirmedDraftAsync(Day(context));
        reflection.MarkStaleByLateInput(context.Clock.UtcNow);
        await context.Reflections.UpdateAsync(reflection, CancellationToken.None);

        var result = await context.Request.ExecuteAsync(
            Day(context),
            target.Id,
            PublicationVisibility.Draft,
            actor: "owner",
            replaceExistingFile: false,
            manual: true,
            CancellationToken.None);

        Assert.IsFalse(result.Queued);
        Assert.AreEqual("publication.reflection_not_confirmed", result.Code);
        Assert.AreEqual(0, await context.Database.CountAsync("publication"));
    }

    [TestMethod]
    public async Task A_manual_publish_uploads_the_confirmed_version_and_records_what_it_sent()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        var (reflection, version) = await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context),
            target.Id,
            PublicationVisibility.Draft,
            actor: "device:abc",
            replaceExistingFile: false,
            manual: true,
            CancellationToken.None);

        Assert.IsTrue(request.Queued, request.Detail);
        Assert.AreEqual(version.Id, request.Publication!.ReflectionVersionId);
        Assert.AreEqual(PublicationTrigger.Manual, request.Publication.Trigger);

        await context.QueueAndRunAsync(request.Publication);

        var stored = await context.Publications.FindByIdAsync(request.Publication.Id, CancellationToken.None);
        Assert.IsNotNull(stored);
        Assert.AreEqual(PublicationStatus.DraftUploaded, stored.Status);
        Assert.IsNotNull(stored.RemoteId);

        // §11.1: the audit trail is written when the attempt runs, so it names who caused it.
        Assert.AreEqual("device:abc", stored.TriggeredBy);
        Assert.IsNotNull(stored.TriggeredAtUtc);
        Assert.IsNotNull(stored.PublishedContentHash, "Without it a later difference check has nothing to compare.");

        Assert.AreEqual(1, context.Remote.CreateCalls);
        Assert.AreEqual("draft", context.Remote.WrittenStatuses[0], "The default outcome is a private draft.");
        StringAssert.Contains(context.Remote.WrittenContents[0], "<p>第一段。</p>");
        Assert.AreEqual(reflection.Id, stored.ReflectionId);
    }

    [TestMethod]
    public async Task Asking_twice_for_the_same_version_and_target_queues_one_publication()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        await context.SeedConfirmedDraftAsync(Day(context));

        var first = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        var second = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        Assert.AreEqual(first.Job!.Id, second.Job!.Id);
        Assert.AreEqual(1, await context.Database.CountAsync("publication"));
    }

    /// <summary>
    /// §17.2: WordPress 重试不产生重复文章. Once an upload has succeeded, every later attempt must address the
    /// article it created rather than making another one.
    /// </summary>
    [TestMethod]
    public async Task A_retry_updates_the_article_instead_of_creating_a_second_one()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        // First attempt: the site is unreachable. The record says so, and one version to one target is still one row.
        context.Remote.FailWith = new TransientExternalFailureException("publication.network", "offline");
        await Assert.ThrowsExceptionAsync<TransientExternalFailureException>(async () =>
            await context.Run.ExecuteAsync(request.Publication!.Id, new PublicationPayload(false), CancellationToken.None));

        var failed = await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.Failed, failed!.Status, "The record follows reality on every failed attempt.");
        Assert.IsNull(failed.RemoteId, "Nothing was created, so there is no remote article yet.");
        Assert.AreEqual(1, await context.Database.CountAsync("publication"), "One version, one target, one record.");

        // The job retries the same attempt: the failure clears and the article is created.
        await context.Run.ExecuteAsync(request.Publication.Id, new PublicationPayload(false), CancellationToken.None);

        var uploaded = await context.Publications.FindByIdAsync(request.Publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.DraftUploaded, uploaded!.Status);
        Assert.IsNotNull(uploaded.RemoteId);

        var createsAfterFirstUpload = context.Remote.CreateCalls;
        var remoteId = uploaded.RemoteId;

        // A re-export is a new round, and it must land on the same article.
        uploaded.RequeueForReExport(PublicationVisibility.Draft, "owner", context.Clock.UtcNow);
        await context.Publications.UpdateAsync(uploaded, CancellationToken.None);
        await context.Run.ExecuteAsync(uploaded.Id, new PublicationPayload(false), CancellationToken.None);

        Assert.AreEqual(createsAfterFirstUpload, context.Remote.CreateCalls, "A re-export must never create a second article.");
        Assert.AreEqual(1, context.Remote.UpdateCalls);
        Assert.AreEqual(
            remoteId,
            (await context.Publications.FindByIdAsync(uploaded.Id, CancellationToken.None))!.RemoteId,
            "The remote id is what makes the next attempt an update.");
    }
    [TestMethod]
    public async Task A_window_that_elapsed_expires_and_publishes_nothing()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog", automatic: true);
        var contentDate = Day(context);
        await context.SeedConfirmedDraftAsync(contentDate);

        // The slot is the day after the content day at 08:00 local; the clock is placed past its window.
        var slot = context.Calendar.AtLocalTime(contentDate.AddDays(1), new TimeOnly(8, 0));
        context.Clock.UtcNow = slot + TimeSpan.FromMinutes(121);

        var request = await context.Request.ExecuteAsync(
            contentDate, target.Id, PublicationVisibility.Public, "system:scheduler", false, false, CancellationToken.None);

        Assert.IsTrue(request.Queued, request.Detail);

        // The slot arrived long ago, so the window decides: running the queued publication expires it rather than
        // publishing late (§11.1, A.2).
        await context.Run.ExecuteAsync(
            request.Publication!.Id,
            new PublicationPayload(false),
            CancellationToken.None);

        var expired = await context.Publications.FindByIdAsync(request.Publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.Expired, expired!.Status);
        Assert.AreEqual(0, context.Remote.CreateCalls + context.Remote.UpdateCalls, "§14: 超窗不得静默补发.");

        // §12: the user is told, because the whole point of the window is that a human finds out.
        var jobs = await context.Jobs.ListRecentAsync(20, CancellationToken.None);
        Assert.IsTrue(
            jobs.Any(job => job.JobType == JobType.Notification),
            "An expiry has to notify; silence is the one outcome the window exists to prevent.");
    }

    [TestMethod]
    public async Task An_unattended_run_is_not_queued_before_its_slot()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog", automatic: true);
        var contentDate = Day(context);
        await context.SeedConfirmedDraftAsync(contentDate);

        // §11.1: the slot for a day is the following day at the configured time, computed from the content day.
        var slot = context.Calendar.AtLocalTime(contentDate.AddDays(1), new TimeOnly(8, 0));
        context.Clock.UtcNow = slot - TimeSpan.FromMinutes(1);

        var request = await context.Request.ExecuteAsync(
            contentDate, target.Id, PublicationVisibility.Draft, "system:scheduler", false, false, CancellationToken.None);

        Assert.IsFalse(request.Queued);
        Assert.AreEqual("publication.slot_not_due", request.Code);
        Assert.AreEqual(0, await context.Database.CountAsync("publication"));
    }

    [TestMethod]
    public async Task An_unattended_run_without_the_opt_in_uploads_a_draft_even_when_public_was_asked_for()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog", automatic: false);
        var contentDate = Day(context);
        await context.SeedConfirmedDraftAsync(contentDate);

        var slot = context.Calendar.AtLocalTime(contentDate.AddDays(1), new TimeOnly(8, 0));
        context.Clock.UtcNow = slot + TimeSpan.FromMinutes(5);

        // Create the publication the scheduler would have created, then run it.
        var reflection = await context.Reflections.FindByContentDateAsync(contentDate, CancellationToken.None);
        var publication = Publication.Create(
            PublicationId.New(),
            reflection!.Id,
            reflection.ConfirmedVersionId!.Value,
            target.Id,
            PublicationTrigger.Automatic,
            slot,
            PublicationVisibility.Public);

        await context.Publications.AddAsync(publication, CancellationToken.None);
        await context.Run.ExecuteAsync(publication.Id, new PublicationPayload(false), CancellationToken.None);

        var stored = await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.DraftUploaded, stored!.Status, "No opt-in means a draft, never a public post.");
        Assert.AreEqual("draft", context.Remote.WrittenStatuses[0]);
    }

    [TestMethod]
    public async Task An_opted_in_target_publishes_publicly_when_the_slot_runs()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog", automatic: true);
        var contentDate = Day(context);
        await context.SeedConfirmedDraftAsync(contentDate);

        var slot = context.Calendar.AtLocalTime(contentDate.AddDays(1), new TimeOnly(8, 0));
        context.Clock.UtcNow = slot + TimeSpan.FromMinutes(5);

        var reflection = await context.Reflections.FindByContentDateAsync(contentDate, CancellationToken.None);
        var publication = Publication.Create(
            PublicationId.New(),
            reflection!.Id,
            reflection.ConfirmedVersionId!.Value,
            target.Id,
            PublicationTrigger.Automatic,
            slot,
            PublicationVisibility.Public);

        await context.Publications.AddAsync(publication, CancellationToken.None);
        await context.Run.ExecuteAsync(publication.Id, new PublicationPayload(false), CancellationToken.None);

        var stored = await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.Published, stored!.Status);
        Assert.AreEqual("publish", context.Remote.WrittenStatuses[0]);
    }

    [TestMethod]
    public async Task New_material_invalidates_a_pending_publication()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        var (reflection, _) = await context.SeedConfirmedDraftAsync(Day(context));

        var publication = Publication.Create(
            PublicationId.New(),
            reflection.Id,
            reflection.ConfirmedVersionId!.Value,
            target.Id,
            PublicationTrigger.Automatic,
            context.Clock.UtcNow,
            PublicationVisibility.Draft);

        await context.Publications.AddAsync(publication, CancellationToken.None);

        // §11.1: 夜间新增素材使待发布版本失效.
        reflection.MarkStaleByLateInput(context.Clock.UtcNow);
        await context.Reflections.UpdateAsync(reflection, CancellationToken.None);

        var sweep = await context.Schedule.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, sweep.Superseded);

        var stored = await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.Superseded, stored!.Status);
    }

    [TestMethod]
    public async Task A_remote_edited_on_the_site_is_reported_as_diverged()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var uploaded = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;

        var inSync = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(inSync.Comparison.InSync, "Nothing has moved yet.");

        context.Remote.EditRemotely(uploaded.RemoteId!, "改过的标题", "<p>在站点上改过</p>");

        var diverged = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(diverged.Comparison.RemoteChecked);
        Assert.IsTrue(diverged.Comparison.RemoteChanged);
        Assert.IsFalse(diverged.Comparison.LocalChanged);
        Assert.AreEqual("改过的标题", diverged.Remote!.Title);

        // "Keep both" is a decision, so it stops being reported as news (§11.1).
        await context.Resolve.ExecuteAsync(uploaded.Id, RemoteDivergenceAction.KeepBoth, "owner", false, CancellationToken.None);

        var acknowledged = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(acknowledged.Comparison.InSync);
    }

    [TestMethod]
    public async Task Pulling_brings_the_remote_text_into_the_draft_as_a_protected_version()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        var (reflection, version) = await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var uploaded = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;

        context.Remote.EditRemotely(uploaded.RemoteId!, "站点上的标题", "<p>站点上写的第一段。</p><p>第二段。</p>");

        await context.Resolve.ExecuteAsync(request.Publication.Id, RemoteDivergenceAction.Pull, "owner", false, CancellationToken.None);

        var reloaded = await context.Reflections.FindByContentDateAsync(Day(context), CancellationToken.None);
        Assert.AreNotEqual(version.Id, reloaded!.WorkingVersionId, "A pull installs a new version.");
        Assert.AreEqual(version.Id, reloaded.InitialVersionId, "§6.4: the first version is still kept.");

        var pulled = await context.Reflections.FindVersionAsync(reloaded.WorkingVersionId!.Value, CancellationToken.None);
        Assert.AreEqual("站点上的标题", pulled!.Title);
        Assert.AreEqual("站点上写的第一段。\n\n第二段。", pulled.Body, "The HTML is turned back into paragraphs.");
        Assert.IsTrue(pulled.HasManualEdits, "Text a person typed on the site must not be rotated away silently.");
        Assert.AreEqual(reflection.Id, reloaded.Id);
    }

    [TestMethod]
    public async Task A_markdown_target_writes_a_file_named_after_the_content_day()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", PublishTargetType.Markdown, destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context), title: "今天的记录");

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);

        var stored = await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.DraftUploaded, stored!.Status);
        Assert.AreEqual("2026-03-11-今天的记录.md", stored.RemoteId);

        var path = Path.Combine(context.Paths.MarkdownPath, "drafts", stored.RemoteId!);
        Assert.IsTrue(File.Exists(path));

        var content = await File.ReadAllTextAsync(path);
        StringAssert.Contains(content, "title: \"今天的记录\"");
        StringAssert.Contains(content, "date: 2026-03-11 00:00:00");
        StringAssert.Contains(content, "draft: true");
        StringAssert.Contains(content, "第一段。");
    }

    /// <summary>§11.2: 再次导出默认创建带版本号的新文件.</summary>
    [TestMethod]
    public async Task Exporting_the_same_version_again_creates_a_versioned_file()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", PublishTargetType.Markdown, destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var first = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!.RemoteId;

        // Re-export without asking to replace: the default is a new file.
        var reexport = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", replaceExistingFile: false, manual: true, CancellationToken.None);

        await context.QueueAndRunAsync(reexport.Publication!, replaceExistingFile: false);

        var second = (await context.Publications.FindByIdAsync(reexport.Publication!.Id, CancellationToken.None))!.RemoteId;

        Assert.AreNotEqual(first, second);
        Assert.AreEqual("2026-03-11-今天的记录-2.md", second);
        Assert.IsTrue(File.Exists(Path.Combine(context.Paths.MarkdownPath, "drafts", first!)));
        Assert.IsTrue(File.Exists(Path.Combine(context.Paths.MarkdownPath, "drafts", second!)));
    }

    /// <summary>§11.2: 不得静默覆盖…被外部修改的文件.</summary>
    [TestMethod]
    public async Task A_file_edited_outside_the_product_is_never_overwritten()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", PublishTargetType.Markdown, destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var stored = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;

        var path = Path.Combine(context.Paths.MarkdownPath, "drafts", stored.RemoteId!);
        await File.WriteAllTextAsync(path, "我在编辑器里改过这个文件了。");

        var reexport = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", replaceExistingFile: true, manual: true, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(async () =>
            await context.Run.ExecuteAsync(
                reexport.Publication!.Id,
                new PublicationPayload(ReplaceExistingFile: true),
                CancellationToken.None));

        Assert.AreEqual(
            "我在编辑器里改过这个文件了。",
            await File.ReadAllTextAsync(path),
            "The hand-edited file must still be exactly as the user left it.");
    }

    [TestMethod]
    public async Task A_file_this_instance_never_wrote_is_never_overwritten()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        await context.AddTargetAsync("hexo", PublishTargetType.Markdown, destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var drafts = Path.Combine(context.Paths.MarkdownPath, "drafts");
        Directory.CreateDirectory(drafts);
        var occupied = Path.Combine(drafts, "2026-03-11-今天的记录.md");
        await File.WriteAllTextAsync(occupied, "这是我手写的文章。");

        // A target that has never written here, asked to export, must pick a free name rather than that one.
        var result = await context.Markdown.WriteAsync(
            new MarkdownWriteRequest(
                drafts,
                "2026-03-11-今天的记录",
                "内容",
                PreviousFileName: null,
                PreviousContentHash: null,
                ReplaceExisting: true),
            CancellationToken.None);

        Assert.AreEqual(MarkdownWritePlan.CreateNewFile, result.Plan);
        Assert.AreEqual("2026-03-11-今天的记录-2.md", result.FileName);
        Assert.AreEqual("这是我手写的文章。", await File.ReadAllTextAsync(occupied));
    }

    /// <summary>§12: 邮件失败不能回滚文章、触发重复生成或改变发布结果.</summary>
    [TestMethod]
    public async Task A_mail_failure_cannot_change_what_was_published()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog", automatic: true);
        var contentDate = Day(context);
        await context.SeedConfirmedDraftAsync(contentDate);

        var slot = context.Calendar.AtLocalTime(contentDate.AddDays(1), new TimeOnly(8, 0));
        context.Clock.UtcNow = slot + TimeSpan.FromMinutes(5);
        context.Email.FailWith = new TransientExternalFailureException("notification.smtp_failed", "relay down");

        var reflection = await context.Reflections.FindByContentDateAsync(contentDate, CancellationToken.None);
        var publication = Publication.Create(
            PublicationId.New(),
            reflection!.Id,
            reflection.ConfirmedVersionId!.Value,
            target.Id,
            PublicationTrigger.Automatic,
            slot,
            PublicationVisibility.Public);

        await context.Publications.AddAsync(publication, CancellationToken.None);
        await context.Run.ExecuteAsync(publication.Id, new PublicationPayload(false), CancellationToken.None);

        // The article went out, the publication records it, and the notification job is the only thing that failed.
        Assert.AreEqual(1, context.Remote.CreateCalls);
        var stored = await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.Published, stored!.Status);

        var notification = (await context.Jobs.ListRecentAsync(20, CancellationToken.None))
            .First(job => job.JobType == JobType.Notification);

        await Assert.ThrowsExceptionAsync<TransientExternalFailureException>(async () =>
            await context.SendNotifications.ExecuteAsync(notification.Payload, CancellationToken.None));

        Assert.AreEqual(0, context.Email.Sent.Count);
        Assert.AreEqual(
            PublicationStatus.Published,
            (await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None))!.Status,
            "A mail failure must leave the publication exactly as it was.");
    }

    [TestMethod]
    public async Task A_notification_is_sent_once_and_carries_no_draft_text()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var day = Day(context);

        var version = ReflectionVersionId.New();
        var queued = await context.QueueNotifications.QueueDraftReadyAsync(day, version, "今天的记录", CancellationToken.None);
        var again = await context.QueueNotifications.QueueDraftReadyAsync(day, version, "今天的记录", CancellationToken.None);

        Assert.IsTrue(queued);
        Assert.IsTrue(again, "Asking again is not an error; the key is what makes it a no-op.");

        var notifications = (await context.Jobs.ListRecentAsync(20, CancellationToken.None))
            .Where(job => job.JobType == JobType.Notification)
            .ToArray();

        Assert.AreEqual(1, notifications.Length, "One notification per event, however often the check runs.");

        await context.SendNotifications.ExecuteAsync(notifications[0].Payload, CancellationToken.None);

        Assert.AreEqual(1, context.Email.Sent.Count);
        var message = context.Email.Sent[0];
        Assert.AreEqual("owner@example.test", message.To);
        StringAssert.Contains(message.Subject, "2026-03-11");

        // §16's spirit: mail travels unencrypted, so it names the day and links to the instance but never carries
        // the user's writing.
        StringAssert.Contains(message.Body, "今天的记录");
        Assert.IsFalse(message.Body.Contains("第一段", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task A_disabled_event_queues_nothing()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        context.Notifications.Settings = new NotificationSettings("owner@example.test", null)
        {
            Events = new Dictionary<NotificationEvent, bool> { [NotificationEvent.DraftReady] = false },
        };

        var queued = await context.QueueNotifications
            .QueueDraftReadyAsync(Day(context), ReflectionVersionId.New(), "标题", CancellationToken.None);

        Assert.IsFalse(queued);
        Assert.AreEqual(0, await context.Database.CountAsync("processing_job"));
    }

    [TestMethod]
    public async Task An_expired_publication_can_be_put_back_in_the_queue_by_hand()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("blog");
        await context.SeedConfirmedDraftAsync(Day(context));

        var reflection = await context.Reflections.FindByContentDateAsync(Day(context), CancellationToken.None);

        var publication = Publication.Create(
            PublicationId.New(),
            reflection!.Id,
            reflection.ConfirmedVersionId!.Value,
            target.Id,
            PublicationTrigger.Automatic,
            context.Clock.UtcNow - TimeSpan.FromHours(5),
            PublicationVisibility.Draft);

        publication.Expire(context.Clock.UtcNow);
        await context.Publications.AddAsync(publication, CancellationToken.None);

        // §11.1: Expired 只能由用户手动重试或重新确认后回到队列.
        var (requeued, job) = await context.Retry.ExecuteAsync(publication.Id, "owner", CancellationToken.None);

        Assert.AreEqual(PublicationStatus.Queued, requeued.Status);
        Assert.AreEqual("owner", requeued.TriggeredBy);
        Assert.AreEqual(0, requeued.AttemptCount);
        Assert.AreEqual(0, requeued.ExportRound);

        await context.Run.ExecuteAsync(publication.Id, new PublicationPayload(false), CancellationToken.None);

        Assert.AreEqual(
            PublicationStatus.DraftUploaded,
            (await context.Publications.FindByIdAsync(publication.Id, CancellationToken.None))!.Status);
    }
}
