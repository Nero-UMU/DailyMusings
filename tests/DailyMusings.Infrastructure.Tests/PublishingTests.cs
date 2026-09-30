using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
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
/// These are the checks that decide what reaches the user's blog, so they are asserted end to end rather than
/// through a mock of our own interface: the target is a real directory on disk, the queue, the records, the files
/// and the settings are the real ones. The only thing doubled is the mail transport.
/// </para>
/// </summary>
[TestClass]
public class PublishingTests
{
    private static ContentDate Day(PublishingTestContext context) => ContentDate.From(context.Today);

    /// <summary>Where an exported file lands. A null directory means the instance's markdown root.</summary>
    private static string ExportedPath(PublishingTestContext context, string? directory, string fileName) =>
        Path.Combine(context.Paths.MarkdownPath, directory ?? string.Empty, fileName);

    private static async Task<string> ReadExportedAsync(
        PublishingTestContext context,
        string? directory,
        string fileName) =>
        await File.ReadAllTextAsync(ExportedPath(context, directory, fileName));

    /// <summary>Every exported file under a directory, which is how "nothing was written" is asserted.</summary>
    private static string[] ExportedFiles(PublishingTestContext context, string? directory = null)
    {
        var root = Path.Combine(context.Paths.MarkdownPath, directory ?? string.Empty);
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            : [];
    }

    [TestMethod]
    public async Task Only_a_confirmed_draft_can_be_published()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");

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

    /// <summary>
    /// §11.1's per-target opt-in decides whether the unattended run goes public. This is the regression test for a
    /// switch that was stored, audited and shown on the settings page while being unreachable: the scheduler asked
    /// for a draft unconditionally, and the planner publishes publicly only when the record wants public <em>and</em>
    /// the target opted in — so the two conditions could never both hold.
    /// </summary>
    [TestMethod]
    public async Task An_unattended_run_goes_public_when_the_target_opted_in()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");

        target.EnableAutomaticPublish("owner", context.Clock.UtcNow);
        await context.Targets.UpdateAsync(target, CancellationToken.None);

        var day = Day(context);
        await context.SeedConfirmedDraftAsync(day);
        AdvanceToJustAfterThePublishSlot(context, day);

        var result = await context.Schedule.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, result.Queued, "The slot has arrived, so the day is due.");

        var publication = (await context.Publications.ListOutstandingAsync(50, CancellationToken.None)).Single();

        Assert.AreEqual(
            PublicationVisibility.Public,
            publication.RequestedVisibility,
            "An opted-in target means the unattended run was asked for publicly, not for a draft.");
        Assert.AreEqual(
            PublicationIntent.PublishPublicly,
            PublicationPlanner.Decide(
                publication,
                target,
                context.Content.Settings.PublishWindow,
                context.Clock.UtcNow));
    }

    /// <summary>The other half of the switch: without the opt-in the very same run stays a private draft.</summary>
    [TestMethod]
    public async Task An_unattended_run_stays_a_draft_when_the_target_did_not_opt_in()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");

        var day = Day(context);
        await context.SeedConfirmedDraftAsync(day);
        AdvanceToJustAfterThePublishSlot(context, day);

        var result = await context.Schedule.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, result.Queued);

        var publication = (await context.Publications.ListOutstandingAsync(50, CancellationToken.None)).Single();

        Assert.AreEqual(PublicationVisibility.Draft, publication.RequestedVisibility);
        Assert.AreEqual(
            PublicationIntent.UploadDraft,
            PublicationPlanner.Decide(
                publication,
                target,
                context.Content.Settings.PublishWindow,
                context.Clock.UtcNow));
    }

    /// <summary>
    /// 2026-09-29 线上实例的真实配置：生成 23:00、发布 23:16（比生成晚 16 分钟）。旧实现**无条件**按
    /// 「内容日期 +1 天」算发布时刻，于是「当晚 23:16 发布」被推到了 24 小时之后——用户勾了自动发布、稿子也
    /// 确认了，到点却什么都不会发生，而页面又从不显示它算出的时刻，失败只能表现为沉默。
    /// </summary>
    [TestMethod]
    public async Task A_publish_time_later_than_the_generation_time_publishes_the_same_evening()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");
        target.EnableAutomaticPublish("owner", context.Clock.UtcNow);
        await context.Targets.UpdateAsync(target, CancellationToken.None);

        context.Content.Settings = WithTimes(context.Content.Settings, "23:00", "23:16");

        var day = Day(context);
        await context.SeedConfirmedDraftAsync(day);

        var calendar = context.Content.Settings.CreateCalendar();
        context.Clock.UtcNow = calendar.AtLocalTime(day, new TimeOnly(23, 5));

        Assert.AreEqual(
            0,
            (await context.Schedule.ExecuteAsync(CancellationToken.None)).Queued,
            "23:05 还没到 23:16，这一天不该入队。");

        context.Clock.UtcNow = calendar.AtLocalTime(day, new TimeOnly(23, 17));

        Assert.AreEqual(
            1,
            (await context.Schedule.ExecuteAsync(CancellationToken.None)).Queued,
            "23:16 是当天，过了就该入队。");

        var publication = (await context.Publications.ListOutstandingAsync(50, CancellationToken.None)).Single();

        Assert.AreEqual(PublicationTrigger.Automatic, publication.Trigger);
        Assert.AreEqual(
            calendar.AtLocalTime(day, new TimeOnly(23, 16)),
            publication.ScheduledAtUtc,
            "发布时刻必须是当天 23:16，而不是次日 23:16。");
    }

    /// <summary>
    /// 默认组合（23:00 生成、次日 08:00 发布）不能因为上一条改动而变样：发布时刻早于生成时刻时仍然是次日。
    /// </summary>
    [TestMethod]
    public async Task The_default_pair_still_publishes_the_next_morning()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        await context.AddTargetAsync("hexo");

        context.Content.Settings = WithTimes(context.Content.Settings, "23:00", "08:00");

        var day = Day(context);
        await context.SeedConfirmedDraftAsync(day);

        var calendar = context.Content.Settings.CreateCalendar();
        context.Clock.UtcNow = calendar.AtLocalTime(day, new TimeOnly(23, 30));

        Assert.AreEqual(
            0,
            (await context.Schedule.ExecuteAsync(CancellationToken.None)).Queued,
            "当晚 23:30 还没到次日 08:00。");

        context.Clock.UtcNow = calendar.AtLocalTime(day.AddDays(1), new TimeOnly(8, 1));

        Assert.AreEqual(1, (await context.Schedule.ExecuteAsync(CancellationToken.None)).Queued);

        var publication = (await context.Publications.ListOutstandingAsync(50, CancellationToken.None)).Single();

        Assert.AreEqual(
            calendar.AtLocalTime(day.AddDays(1), new TimeOnly(8, 0)),
            publication.ScheduledAtUtc,
            "默认组合仍然是次日 08:00。");
    }

    /// <summary>同一份设置换两个时刻（ContentSettings 是类而不是 record，所以不能 with）。</summary>
    private static ContentSettings WithTimes(ContentSettings settings, string generation, string publish) => new(
        settings.TimeZoneId,
        TimeOnly.Parse(generation, CultureInfo.InvariantCulture),
        TimeOnly.Parse(publish, CultureInfo.InvariantCulture),
        settings.PublishWindowMinutes,
        settings.AudioRetentionDays,
        settings.ContentRetentionDays,
        settings.DraftDirectory,
        settings.PublishedDirectory,
        settings.HexoFrontMatterTemplate,
        settings.Writing);

    /// <summary>
    /// A person asking again for an already-exported draft decides for itself. That record was created by the
    /// scheduler, so without the requeue path marking it manual the planner kept applying the unattended gate and
    /// answered "queued" while quietly exporting another draft.
    /// </summary>
    [TestMethod]
    public async Task Asking_again_by_hand_makes_an_unattended_record_manual()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts");

        var day = Day(context);
        await context.SeedConfirmedDraftAsync(day);
        AdvanceToJustAfterThePublishSlot(context, day);

        await context.Schedule.ExecuteAsync(CancellationToken.None);

        // The slot exported a private draft, as it must for a target that never opted in.
        var first = (await context.Publications.ListOutstandingAsync(50, CancellationToken.None)).Single();
        await context.QueueAndRunAsync(first);

        var firstRun = (await context.Publications.FindByIdAsync(first.Id, CancellationToken.None))!;
        Assert.AreEqual("draft: true", DraftFlag(await ReadExportedAsync(context, "drafts", firstRun.RemoteId!)));

        // The user now asks for that day to be public.
        var again = await context.Request.ExecuteAsync(
            day,
            target.Id,
            PublicationVisibility.Public,
            actor: "owner",
            replaceExistingFile: false,
            manual: true,
            CancellationToken.None);

        Assert.AreEqual(PublicationRequestOutcome.Created, again.Outcome, again.Detail);
        Assert.AreEqual(
            PublicationTrigger.Manual,
            again.Publication!.Trigger,
            "A human re-request must stop being judged as an unattended run.");

        var run = await context.Run.ExecuteAsync(
            again.Publication!.Id,
            new PublicationPayload(false),
            CancellationToken.None);

        var stored = await context.Publications.FindByIdAsync(again.Publication.Id, CancellationToken.None);

        Assert.AreEqual(
            PublicationRunOutcome.Published,
            run.Outcome,
            $"outcome={run.Outcome} status={stored!.Status} trigger={stored.Trigger} " +
            $"visibility={stored.RequestedVisibility} round={stored.ExportRound}");

        // For a Markdown target the whole of "public" is the front matter's draft flag: Hexo will not generate a
        // post that says it is a draft, so an explicit public request has to reach the file as draft: false.
        Assert.AreEqual(
            "draft: false",
            DraftFlag(await ReadExportedAsync(context, "drafts", stored.RemoteId!)),
            $"The explicit public request has to reach the file as a published post, not as another draft. " +
            $"outcome={run.Outcome} fileName={stored.RemoteId} status={stored.Status}");
    }

    /// <summary>Pulls the single <c>draft:</c> line out of a rendered file's front matter.</summary>
    private static string DraftFlag(string content) =>
        content.Split('\n').First(line => line.StartsWith("draft:", StringComparison.Ordinal)).Trim();

    /// <summary>Moves the clock to the moment the day's publish slot has just opened.</summary>
    private static void AdvanceToJustAfterThePublishSlot(PublishingTestContext context, ContentDate day)
    {
        var settings = context.Content.Settings;

        // 与产品同一套规则（ContentSettings.PublishSlotFor），不在这里重算一遍：重算就会漂移。
        context.Clock.UtcNow = settings.PublishSlotFor(day).AddMinutes(1);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task A_regenerated_unpublished_draft_is_reminded_at_publish_time_without_replacing_the_published_post(
        bool confirmReplacement)
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "posts", automatic: true);
        var day = Day(context);
        var (reflection, publishedVersion) = await context.SeedConfirmedDraftAsync(day);

        var request = await context.Request.ExecuteAsync(
            day,
            target.Id,
            PublicationVisibility.Public,
            "owner",
            replaceExistingFile: false,
            manual: true,
            CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var published = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;
        var publishedPath = ExportedPath(context, "posts", published.RemoteId!);
        Assert.AreEqual(PublicationStatus.Published, published.Status);
        Assert.IsTrue(File.Exists(publishedPath));

        var replacement = await context.RegenerateUnconfirmedAsync(reflection);
        Assert.AreNotEqual(publishedVersion.Id, replacement.Id);

        if (confirmReplacement)
        {
            reflection.Confirm(replacement.Id, context.Clock.UtcNow);
            await context.Reflections.UpdateAsync(reflection, CancellationToken.None);
        }

        Assert.AreEqual(
            confirmReplacement ? ReflectionStatus.Confirmed : ReflectionStatus.ReviewRequired,
            reflection.Status);
        AdvanceToJustAfterThePublishSlot(context, day);

        var firstSweep = await context.Schedule.ExecuteAsync(CancellationToken.None);
        var secondSweep = await context.Schedule.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, firstSweep.Queued, "An unconfirmed replacement must never be published by the scheduler.");
        Assert.AreEqual(0, secondSweep.Queued);
        Assert.AreEqual(
            PublicationStatus.Published,
            (await context.Publications.FindByIdAsync(published.Id, CancellationToken.None))!.Status,
            "Regeneration alone must leave the old published record current.");
        Assert.IsTrue(File.Exists(publishedPath), "Regeneration alone must not delete the published Markdown file.");

        var reminderJobs = (await context.Jobs.ListRecentAsync(50, CancellationToken.None))
            .Where(job => job.JobType == JobType.Notification)
            .Select(job => NotificationPayload.FromJson(job.Payload))
            .Where(payload => payload?.Subject.Contains("未发布", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.AreEqual(1, reminderJobs.Length, "The publish-time reminder must be queued once, even across repeated sweeps.");
        StringAssert.Contains(reminderJobs[0]!.Body, "不会自动用新稿替换");
        Assert.AreEqual(1, ExportedFiles(context, "posts").Length);
    }

    /// <summary>
    /// 后台填的目录**以映射进来的数据目录为根**（附录 A.31）：映射了
    /// <c>/srv/dailymusings/data:/var/lib/dailymusings</c> 之后填 <c>aaa/bbb/posts</c>，稿件就落在
    /// <c>/srv/dailymusings/data/aaa/bbb/posts</c>。这条测试钉住「填什么就写到哪里」——中间不该再冒出一层没人
    /// 知道的目录（旧布局里那层叫 <c>markdown/</c>）。
    /// </summary>
    [TestMethod]
    public async Task A_nested_target_directory_is_created_directly_under_the_markdown_root()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "aaa/bbb/posts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context),
            target.Id,
            PublicationVisibility.Public,
            actor: "owner",
            replaceExistingFile: false,
            manual: true,
            CancellationToken.None);

        Assert.IsTrue(request.Queued, request.Detail);
        await context.QueueAndRunAsync(request.Publication!);

        var nested = Path.Combine(context.Paths.MarkdownPath, "aaa", "bbb", "posts");
        var files = Directory.Exists(nested)
            ? Directory.GetFiles(nested, "*.md", SearchOption.AllDirectories)
            : [];

        Assert.AreEqual(1, files.Length, $"填的路径就是宿主上看到的那条相对路径；实际写在 {nested}。");
        Assert.AreEqual(
            files.Length,
            ExportedFiles(context).Length,
            "整棵 Markdown 根下只有这一个文件，说明没有再套一层目录（旧布局那层叫 markdown/）。");
    }

    [TestMethod]
    public async Task A_manual_publish_exports_the_confirmed_version_and_records_what_it_wrote()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts");
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
        Assert.AreEqual("2026-03-11-今天的记录.md", stored.RemoteId);

        // §11.1: the audit trail is written when the attempt runs, so it names who caused it.
        Assert.AreEqual("device:abc", stored.TriggeredBy);
        Assert.IsNotNull(stored.TriggeredAtUtc);
        Assert.IsNotNull(stored.PublishedContentHash, "Without it a later difference check has nothing to compare.");

        var content = await ReadExportedAsync(context, "drafts", stored.RemoteId!);
        StringAssert.Contains(content, "title: \"今天的记录\"");
        StringAssert.Contains(content, "第一段。");
        Assert.AreEqual("draft: true", DraftFlag(content), "The default outcome is a private draft.");
        Assert.AreEqual(reflection.Id, stored.ReflectionId);
    }

    [TestMethod]
    public async Task Asking_twice_for_the_same_version_and_target_queues_one_publication()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");
        await context.SeedConfirmedDraftAsync(Day(context));

        var first = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        var second = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        Assert.AreEqual(first.Job!.Id, second.Job!.Id);
        Assert.AreEqual(1, await context.Database.CountAsync("publication"));
    }

    /// <summary>
    /// §14: one version to one target happens once, and a failed attempt is visible while it is still being
    /// retried. §11.2's refusal is the failure used here because it is the one a Markdown target really has:
    /// somebody edited the exported file and nobody confirmed an overwrite, so the queue must not "try harder"
    /// over their work — but the record has to say Failed rather than stay InProgress, and the very same attempt
    /// must succeed once the file is ours again.
    /// </summary>
    [TestMethod]
    public async Task A_failed_attempt_is_recorded_and_the_same_attempt_can_succeed_later()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);

        var uploaded = await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.DraftUploaded, uploaded!.Status);
        var fileName = uploaded.RemoteId;
        Assert.IsNotNull(fileName);

        // Somebody edits the exported file by hand, then a re-export is attempted without any explicit decision.
        var path = ExportedPath(context, "drafts", fileName!);
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, "我在编辑器里改过这个文件了。");

        uploaded.RequeueForReExport(PublicationVisibility.Draft, "owner", context.Clock.UtcNow);
        await context.Publications.UpdateAsync(uploaded, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(async () =>
            await context.Run.ExecuteAsync(uploaded.Id, new PublicationPayload(ReplaceExistingFile: false), CancellationToken.None));

        var failed = await context.Publications.FindByIdAsync(uploaded.Id, CancellationToken.None);
        Assert.AreEqual(
            PublicationStatus.Failed,
            failed!.Status,
            "The record follows reality on every failed attempt, so the user can see it while the job retries.");
        Assert.AreEqual(1, await context.Database.CountAsync("publication"), "One version, one target, one record.");
        Assert.AreEqual("我在编辑器里改过这个文件了。", await File.ReadAllTextAsync(path));

        // The user puts the file back the way it was; the same attempt now goes through and lands on the same file.
        await File.WriteAllTextAsync(path, original);
        await context.Run.ExecuteAsync(uploaded.Id, new PublicationPayload(ReplaceExistingFile: true), CancellationToken.None);

        var recovered = await context.Publications.FindByIdAsync(uploaded.Id, CancellationToken.None);
        Assert.AreEqual(PublicationStatus.DraftUploaded, recovered!.Status);
        Assert.AreEqual(fileName, recovered.RemoteId, "A retry must land on the file it wrote, not make another one.");
        Assert.AreEqual(1, ExportedFiles(context, "drafts").Length);
    }

    [TestMethod]
    public async Task A_window_that_elapsed_expires_and_publishes_nothing()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts", automatic: true);
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
        Assert.AreEqual(0, ExportedFiles(context, "drafts").Length, "§14: 超窗不得静默补发.");

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
        var target = await context.AddTargetAsync("hexo", automatic: true);
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
    public async Task An_unattended_run_without_the_opt_in_stays_a_draft_even_when_public_was_asked_for()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts", automatic: false);
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
        Assert.AreEqual(
            "draft: true",
            DraftFlag(await ReadExportedAsync(context, "drafts", stored.RemoteId!)),
            "Without the opt-in the file must still tell Hexo it is a draft.");
    }

    [TestMethod]
    public async Task An_opted_in_target_publishes_publicly_when_the_slot_runs()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts", automatic: true);
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
        Assert.AreEqual(
            "draft: false",
            DraftFlag(await ReadExportedAsync(context, "drafts", stored.RemoteId!)),
            "An opted-in unattended run has to produce a post Hexo will actually generate.");
    }

    [TestMethod]
    public async Task New_material_invalidates_a_pending_publication()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo");
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

    /// <summary>§11.1: 用户可主动检查远程差异. For a file target "the remote" is the exported file itself.</summary>
    [TestMethod]
    public async Task An_exported_file_edited_on_disk_is_reported_as_diverged()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var uploaded = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;

        var inSync = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(inSync.Comparison.InSync, "Nothing has moved yet.");

        await File.WriteAllTextAsync(
            ExportedPath(context, "drafts", uploaded.RemoteId!),
            "我在编辑器里改过这个文件了。");

        var diverged = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(diverged.Comparison.RemoteChecked);
        Assert.IsTrue(diverged.Comparison.RemoteChanged);
        Assert.IsFalse(diverged.Comparison.LocalChanged);

        // "Keep both" is a decision, so it stops being reported as news (§11.1).
        await context.Resolve.ExecuteAsync(uploaded.Id, RemoteDivergenceAction.KeepBoth, "owner", false, CancellationToken.None);

        var acknowledged = await context.Check.ExecuteAsync(uploaded.Id, CancellationToken.None);
        Assert.IsTrue(acknowledged.Comparison.InSync);
    }

    /// <summary>
    /// Pulling was one of the three answers to a difference. With the export being a file in a directory the user
    /// owns, there is nothing to pull back: copying the file into the draft would make that directory a second
    /// source of truth, which is the thing §11.2 exists to avoid. The action stays in the contract and refuses
    /// explicitly rather than silently doing nothing.
    /// </summary>
    [TestMethod]
    public async Task Pulling_an_export_back_into_the_draft_is_refused()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", destination: "drafts");
        var (reflection, version) = await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);

        var refusal = await Assert.ThrowsExceptionAsync<UseCaseException>(async () =>
            await context.Resolve.ExecuteAsync(
                request.Publication!.Id,
                RemoteDivergenceAction.Pull,
                "owner",
                false,
                CancellationToken.None));

        Assert.AreEqual("publication.pull.not_supported", refusal.Code);

        var reloaded = await context.Reflections.FindByContentDateAsync(Day(context), CancellationToken.None);
        Assert.AreEqual(version.Id, reloaded!.WorkingVersionId, "A refused pull must not install a version.");
        Assert.AreEqual(reflection.Id, reloaded.Id);
    }

    [TestMethod]
    public async Task An_export_is_named_after_the_content_day()
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

        var content = await ReadExportedAsync(context, "drafts", stored.RemoteId!);
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
        Assert.IsTrue(File.Exists(ExportedPath(context, "drafts", first!)));
        Assert.IsTrue(File.Exists(ExportedPath(context, "drafts", second!)));
    }

    /// <summary>
    /// §11.2 plus §17.3 step 7: a re-export nobody confirmed must not touch a file the user edited — but the
    /// explicit 覆盖, which the client only offers after the divergence has been reported, has to be carried out.
    /// </summary>
    [TestMethod]
    public async Task A_hand_edited_file_is_refused_until_the_user_says_overwrite()
    {
        await using var context = await PublishingTestContext.CreateAsync();
        var target = await context.AddTargetAsync("hexo", PublishTargetType.Markdown, destination: "drafts");
        await context.SeedConfirmedDraftAsync(Day(context));

        var request = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", false, true, CancellationToken.None);

        await context.QueueAndRunAsync(request.Publication!);
        var stored = (await context.Publications.FindByIdAsync(request.Publication!.Id, CancellationToken.None))!;

        var path = ExportedPath(context, "drafts", stored.RemoteId!);
        var exported = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, "我在编辑器里改过这个文件了。");

        // A re-export with no explicit decision refuses, and now names the right reason: this instance wrote the
        // file and something changed it afterwards, which is not the same as "we never wrote it".
        var reexport = await context.Request.ExecuteAsync(
            Day(context), target.Id, PublicationVisibility.Draft, "owner", replaceExistingFile: false, manual: true, CancellationToken.None);

        var refused = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(async () =>
            await context.Run.ExecuteAsync(
                reexport.Publication!.Id,
                new PublicationPayload(ReplaceExistingFile: false),
                CancellationToken.None));

        Assert.AreEqual("markdown.file.modified_externally", refused.Code);
        Assert.AreEqual(
            "我在编辑器里改过这个文件了。",
            await File.ReadAllTextAsync(path),
            "The hand-edited file must still be exactly as the user left it.");

        // The user has now been shown the divergence and chooses 覆盖: the local version wins, in place.
        await context.Run.ExecuteAsync(
            reexport.Publication!.Id,
            new PublicationPayload(ReplaceExistingFile: true),
            CancellationToken.None);

        var recovered = (await context.Publications.FindByIdAsync(reexport.Publication!.Id, CancellationToken.None))!;
        Assert.AreEqual(PublicationStatus.DraftUploaded, recovered.Status);
        Assert.AreEqual(
            stored.RemoteId,
            recovered.RemoteId,
            "覆盖 rewrites the file this instance wrote; it does not make a second one.");
        Assert.AreEqual(exported, await File.ReadAllTextAsync(path), "The file now holds what the instance wrote.");
        Assert.AreEqual(1, ExportedFiles(context, "drafts").Length);
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
        var target = await context.AddTargetAsync("hexo", destination: "drafts", automatic: true);
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

        // The file went out, the publication records it, and the notification job is the only thing that failed.
        Assert.AreEqual(1, ExportedFiles(context, "drafts").Length);
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
        var target = await context.AddTargetAsync("hexo", destination: "drafts");
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
