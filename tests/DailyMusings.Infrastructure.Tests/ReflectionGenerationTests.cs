using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The generation pipeline end to end at the use-case level, over real storage
/// (docs/开发指导.md §7, §8.4, §17.1).
/// <para>
/// These are the rules that decide what gets written and what may be rewritten, so they are asserted through the
/// same code paths the scheduler and the API drive — including the codes a client is expected to act on.
/// </para>
/// </summary>
[TestClass]
public class ReflectionGenerationTests
{
    [TestMethod]
    public async Task A_day_with_material_is_written_once_and_its_citations_are_resolved()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        var entry = await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today,
            manual: true,
            ignoreTranscriptionFailures: false,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        Assert.IsTrue(request.Decision.Allowed, request.Decision.Detail);
        Assert.IsNotNull(request.Job);
        Assert.AreEqual(JobType.ReflectionGeneration, request.Job.JobType);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job.Payload),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);
        Assert.AreEqual(1, context.Client.GenerateCalls);
        Assert.AreEqual(0, result.UnresolvedCitations, "The citation quotes a sentence the body actually contains.");

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.IsNotNull(reflection);
        Assert.AreEqual(ReflectionStatus.ReviewRequired, reflection.Status);
        Assert.AreEqual(reflection.InitialVersionId, reflection.WorkingVersionId);

        var version = await context.Reflections.FindVersionAsync(reflection.WorkingVersionId!.Value, CancellationToken.None);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Sources.Count);
        Assert.AreEqual(entry.Id, version.Sources[0].InputId);

        // Every citation resolves against the text it was produced for, which is the only state in which the
        // client may highlight it precisely.
        Assert.IsTrue(version.CheckSourceDrift().All(item => item.Drift == SourceDrift.Exact));

        // §8.4's second stage is queued by the same transaction that installed the version.
        Assert.IsNotNull(result.CheckJob);
        Assert.AreEqual(JobType.UnsourcedStatementCheck, result.CheckJob.JobType);
    }

    [TestMethod]
    public async Task An_arbitrary_past_day_cannot_be_generated_on_request()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("前天的记录。", contentDate: context.Today.AddDays(-2));

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today.AddDays(-2),
            manual: true,
            ignoreTranscriptionFailures: false,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        Assert.IsFalse(request.Decision.Allowed);
        Assert.AreEqual("reflection.regeneration.date_not_current", request.Decision.Code);
        Assert.IsNull(request.Job, "A refused request must not put anything on the queue.");
    }

    [TestMethod]
    public async Task A_day_with_no_material_never_produces_an_empty_article()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today,
            manual: true,
            ignoreTranscriptionFailures: false,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        Assert.IsFalse(request.Decision.Allowed);
        Assert.AreEqual("reflection.generation.no_inputs", request.Decision.Code);
        Assert.AreEqual(0, await context.Database.CountAsync("reflection"));
    }

    [TestMethod]
    public async Task Asking_twice_for_the_same_round_enqueues_one_job()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var first = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var second = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.AreEqual(first.Job!.Id, second.Job!.Id);
        Assert.AreEqual(1, await context.Database.CountAsync("processing_job"));
    }

    [TestMethod]
    public async Task A_hand_edited_working_version_is_never_silently_overwritten()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (_, edited) = await context.SeedDraftAsync(context.Today, ReflectionStatus.StaleByLateInput, workingVersionHasManualEdits: true);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(AllowOverwriteOfManualEdits: false, GenerationReason.LateInputRegeneration, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.SkippedManualEditsProtected, result.Outcome);
        Assert.AreEqual(0, context.Client.GenerateCalls, "The model must not be called once the refusal is known.");

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(edited.Id, reflection!.WorkingVersionId, "The user's text stays where it is.");
    }

    [TestMethod]
    public async Task An_accepted_overwrite_rotates_the_slots_and_keeps_the_first_version()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (_, edited) = await context.SeedDraftAsync(context.Today, ReflectionStatus.StaleByLateInput, workingVersionHasManualEdits: true);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(AllowOverwriteOfManualEdits: true, GenerationReason.LateInputRegeneration, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(edited.Id, reflection!.InitialVersionId, "The first version ever produced is kept forever.");
        Assert.AreEqual(edited.Id, reflection.PreviousVersionId, "The edited version moves to the previous slot.");
        Assert.AreNotEqual(edited.Id, reflection.WorkingVersionId);
    }

    [TestMethod]
    public async Task An_accepted_overwrite_is_not_swallowed_by_the_refused_attempt_before_it()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (_, edited) = await context.SeedDraftAsync(context.Today, ReflectionStatus.StaleByLateInput, workingVersionHasManualEdits: true);

        // A client that does not know about the hand edit asks first, and the job refuses to rotate.
        var refused = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var skipped = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(refused.Job!.Payload),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.SkippedManualEditsProtected, skipped.Outcome);

        // The user is then told what regenerating would cost and accepts it. The decision has to reach the model:
        // reusing the refused attempt's key would hand back a job that had already finished and rotated nothing.
        var accepted = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: true, CancellationToken.None);

        Assert.IsTrue(accepted.Decision.Allowed, accepted.Decision.Detail);
        Assert.AreNotEqual(refused.Job.Id, accepted.Job!.Id, "The accepted request must not reuse the refusal's job.");

        var rotated = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(accepted.Job.Payload),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, rotated.Outcome);

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(edited.Id, reflection!.PreviousVersionId, "The hand-edited version moves to the previous slot.");
        Assert.AreNotEqual(edited.Id, reflection.WorkingVersionId);
    }

    [TestMethod]
    public async Task A_confirmed_day_can_be_regenerated_when_the_user_asks_it()
    {
        // A.21 的「同一天已有正式稿后再生成新稿」靠这条才走得到：新稿只进工作槽，旧正式稿继续对外，
        // 直到用户再按「继续发布 → 确认替换并发布」（A.40）。
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(false, GenerationReason.Manual, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);
    }

    [TestMethod]
    public async Task A_confirmed_day_is_not_regenerated_by_an_automatic_run()
    {
        // 用户本人的请求可以推翻已确认的稿子，定时任务永远不行。
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(false, GenerationReason.Scheduled, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.SkippedAlreadyConfirmed, result.Outcome);
    }

    /// <summary>
    /// 往日的成稿随生成请求一起交给模型（§8.4，附录 A.41 续记）：窗口内**已发布**的那一版给，没发布的不给。
    /// <para>
    /// 这条只能在用例这一层验：窗口由用户的写作规范决定，取的是 publication 的 Published 状态，
    /// 而"到底交给了模型什么"只有 FakeGenerationClient 的 LastRequest 知道。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Published_articles_inside_the_window_are_handed_to_the_model()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        var yesterday = context.Today.AddDays(-1);

        // 昨天要有输入，SeedDraftAsync 的来源映射才落得下去（它按内容日找输入）。
        await context.CaptureTextAsync("昨天试着记录了一点东西。", yesterday);
        var (yesterdayReflection, yesterdayVersion) =
            await context.SeedDraftAsync(yesterday, ReflectionStatus.Confirmed);

        var target = PublishTarget.Create(PublishTargetId.New(), "hexo", PublishTargetType.Markdown, "posts");
        await context.Targets.AddAsync(target, CancellationToken.None);

        var publication = Publication.Create(
            PublicationId.New(),
            yesterdayReflection.Id,
            yesterdayVersion.Id,
            target.Id,
            PublicationTrigger.Automatic,
            context.Clock.UtcNow,
            PublicationVisibility.Public);

        await context.Publications.AddAsync(publication, CancellationToken.None);
        publication.Begin("system:scheduler", context.Clock.UtcNow);
        publication.CompleteAsPublished("2026-10-01-标题.md", "hash", context.Clock.UtcNow);
        await context.Publications.UpdateAsync(publication, CancellationToken.None);

        // 没有这一条素材就不会走到生成（当天无输入不得创建空文章）。
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(false, GenerationReason.Manual, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);

        var articles = context.Client.LastRequest!.RecentArticles;
        Assert.AreEqual(1, articles.Count, "窗口内只有昨天那一篇已发布的成稿。");
        Assert.AreEqual(yesterday, articles[0].ContentDate);
        Assert.AreEqual("标题", articles[0].Title);
        Assert.AreEqual("第一段内容。\n\n第二段内容。", articles[0].Body);
    }

    /// <summary>窗口设成 0 就一篇都不给：用户可以只要"今天只写今天"。</summary>
    [TestMethod]
    public async Task A_zero_window_hands_the_model_no_previous_articles()
    {
        // 窗口来自用户的写作规范，所以直接把它设成 0 建上下文；生成用例读的就是这个提供者。
        var content = ContentSettings.Default with
        {
            Writing = ContentSettings.Default.Writing with { RecentArticleDays = 0 },
        };

        await using var context = await ReflectionTestContext.CreateAsync(content: content);

        await context.CaptureTextAsync("昨天试着记录了一点东西。", context.Today.AddDays(-1));
        var (reflection, version) = await context.SeedDraftAsync(context.Today.AddDays(-1), ReflectionStatus.Confirmed);

        var target = PublishTarget.Create(PublishTargetId.New(), "hexo", PublishTargetType.Markdown, "posts");
        await context.Targets.AddAsync(target, CancellationToken.None);

        var publication = Publication.Create(
            PublicationId.New(),
            reflection.Id,
            version.Id,
            target.Id,
            PublicationTrigger.Automatic,
            context.Clock.UtcNow,
            PublicationVisibility.Public);

        await context.Publications.AddAsync(publication, CancellationToken.None);
        publication.Begin("system:scheduler", context.Clock.UtcNow);
        publication.CompleteAsPublished("2026-10-01-标题.md", "hash", context.Clock.UtcNow);
        await context.Publications.UpdateAsync(publication, CancellationToken.None);

        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            new ReflectionGenerationPayload(false, GenerationReason.Manual, false),
            CancellationToken.None);

        Assert.AreEqual(ReflectionGenerationOutcome.Generated, result.Outcome);
        Assert.AreEqual(0, context.Client.LastRequest!.RecentArticles.Count, "窗口为 0 时一条成稿都不发。");
    }

    /// <summary>
    /// 用户实际点的是发布设置页的「立即生成稿件」，它走的是请求用例（准入规则）而不是作业处理端。
    /// 两层都要放行，否则用户在弹窗里看到的是拒绝。
    /// </summary>
    [TestMethod]
    public async Task The_generate_button_may_ask_for_a_confirmed_day_again()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today,
            manual: true,
            ignoreTranscriptionFailures: true,
            allowOverwriteOfManualEdits: false,
            CancellationToken.None);

        Assert.IsTrue(request.Decision.Allowed, request.Decision.Detail);
        Assert.IsNotNull(request.Job);
    }

    /// <summary>
    /// §7's staleness rule, exercised through the real ingestion path: material for a day that already has a draft
    /// invalidates that draft, whether it arrives later the same day or the next day.
    /// </summary>
    [TestMethod]
    public async Task Capturing_more_material_invalidates_an_already_produced_draft()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        await context.CaptureTextAsync("刚刚又想起一件事。", idempotencyKey: "late");

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(ReflectionStatus.StaleByLateInput, reflection!.Status);

        // §7 path ②: yesterday's capture only reaches the server today. Its content day comes from the capture
        // instant (A.4/A.5), so it still belongs to yesterday and invalidates yesterday's draft.
        await using var second = await ReflectionTestContext.CreateAsync();
        var yesterday = second.Today.AddDays(-1);
        await second.CaptureTextAsync("昨天的记录。", contentDate: yesterday);
        await second.SeedDraftAsync(yesterday, ReflectionStatus.Confirmed);

        await second.CaptureTextAsync(
            "补记昨天的事。",
            contentDate: yesterday,
            capturedAtUtc: second.Calendar.AtLocalTime(yesterday, new TimeOnly(22, 30)),
            idempotencyKey: "yesterday-late");

        Assert.AreEqual(
            ReflectionStatus.StaleByLateInput,
            (await second.Reflections.FindByContentDateAsync(yesterday, CancellationToken.None))!.Status);
    }

    [TestMethod]
    public async Task A_stale_day_is_queued_for_regeneration_by_the_scheduler_path()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        var (reflection, _) = await context.SeedDraftAsync(context.Today, ReflectionStatus.StaleByLateInput);

        // After the day's slot, which is when the scan would find it.
        context.Clock.UtcNow = context.Calendar.AtLocalTime(context.Today, new TimeOnly(23, 1));

        // §7's only permitted regeneration of an existing day, and it is still today, so it is allowed.
        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: false, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.IsTrue(request.Decision.Allowed, request.Decision.Detail);
        Assert.AreEqual(
            "reflection-generation:2026-03-10#1",
            request.Job!.IdempotencyKey,
            "A regeneration is a new round, or the unique key would refuse it forever.");

        var loaded = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(reflection.Id, loaded!.Id);
    }

    [TestMethod]
    public async Task The_slot_is_not_reached_before_the_configured_local_time()
    {
        // 2026-03-10T04:00Z is 12:00 in Asia/Shanghai, well before the default 23:00 slot.
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: false, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.IsFalse(request.Decision.Allowed);
        Assert.AreEqual("reflection.generation.slot_not_due", request.Decision.Code);

        // The same request after the slot is a different answer.
        context.Clock.UtcNow = context.Calendar.AtLocalTime(context.Today, new TimeOnly(23, 1));

        var afterSlot = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: false, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.IsTrue(afterSlot.Decision.Allowed, afterSlot.Decision.Detail);
    }

    [TestMethod]
    public async Task A_failed_transcription_defers_the_nightly_run_but_not_an_explicit_decision()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var voice = InputEntry.CreateVoice(
            InputEntryId.New(),
            context.Clock.UtcNow,
            480,
            context.Today,
            "media/2026/03/a.m4a",
            TimeSpan.FromSeconds(5));
        voice.BeginTranscription();
        voice.FailTranscription("transcription.timeout");
        await context.Inputs.AddAsync(voice, CancellationToken.None);

        context.Clock.UtcNow = context.Calendar.AtLocalTime(context.Today, new TimeOnly(23, 1));

        var scheduled = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: false, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.AreEqual("reflection.generation.transcription_failures", scheduled.Decision.Code);

        // §7: 用户可选择忽略失败项继续.
        var manual = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: true, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.IsTrue(manual.Decision.Allowed, manual.Decision.Detail);
    }

    [TestMethod]
    public async Task A_draft_cannot_be_confirmed_before_the_source_check_finishes()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        var exception = await Assert.ThrowsExceptionAsync<UseCaseException>(async () =>
            await context.Confirm.ExecuteAsync(
                context.Today,
                new Application.Reflections.ConfirmReflectionRequest(AcceptedUnsourcedClaims: true),
                CancellationToken.None));

        Assert.AreEqual("reflection.confirm.source_check_pending", exception.Code);
    }

    [TestMethod]
    public async Task The_second_stage_records_findings_and_stamps_the_check()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        context.Client.ExtraParagraph = "我记得那天的风很大。";
        context.Client.SuspiciousSentences.Add("我记得那天的风很大。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var generated = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        var findings = await context.Check.ExecuteAsync(generated.Version!.Id, CancellationToken.None);

        Assert.AreEqual(1, findings, "The sentence the checker flagged is quoted verbatim from the body.");

        var version = await context.Reflections.FindVersionAsync(generated.Version.Id, CancellationToken.None);
        Assert.AreEqual(1, version!.UnsourcedClaims.Count);
        Assert.AreEqual(1, version.UnsourcedClaims[0].BlockIndex, "The flagged sentence is in the second paragraph.");
        Assert.IsNotNull(version.SourcesCheckedAtUtc);
    }

    [TestMethod]
    public async Task Confirming_a_draft_with_findings_requires_acknowledging_them()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");
        context.Client.ExtraParagraph = "我记得那天的风很大。";
        context.Client.SuspiciousSentences.Add("我记得那天的风很大。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var generated = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        await context.Check.ExecuteAsync(generated.Version!.Id, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<UseCaseException>(async () =>
            await context.Confirm.ExecuteAsync(
                context.Today,
                new Application.Reflections.ConfirmReflectionRequest(AcceptedUnsourcedClaims: false),
                CancellationToken.None));

        // §8.4: the findings are a warning, never a block — once acknowledged, the user may confirm.
        var confirmed = await context.Confirm.ExecuteAsync(
            context.Today,
            new Application.Reflections.ConfirmReflectionRequest(AcceptedUnsourcedClaims: true),
            CancellationToken.None);

        Assert.AreEqual(ReflectionStatus.Confirmed, confirmed.Status);
        Assert.AreEqual(confirmed.WorkingVersionId, confirmed.ConfirmedVersionId);
    }

    [TestMethod]
    public async Task Switching_the_working_version_is_reported_rather_than_hidden()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var first = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        await context.Check.ExecuteAsync(first.Version!.Id, CancellationToken.None);

        // A second round, as §7's staleness path would produce.
        var secondRequest = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var second = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(secondRequest.Job!.Payload),
            CancellationToken.None);

        Assert.AreEqual(first.Version!.Id, second.Reflection!.InitialVersionId);
        Assert.AreEqual(first.Version.Id, second.Reflection.PreviousVersionId);
        Assert.AreEqual(second.Version!.Id, second.Reflection.WorkingVersionId);

        var switched = await context.SwitchVersion.ExecuteAsync(
            context.Today,
            first.Version.Id,
            CancellationToken.None);

        Assert.AreEqual(first.Version.Id, switched.WorkingVersionId);
        Assert.AreEqual(second.Version.Id, switched.PreviousVersionId);
        Assert.AreEqual(first.Version.Id, switched.InitialVersionId);

        // The client can see that what it confirmed is no longer what it is editing.
        var view = await context.GetReflection.ExecuteAsync(context.Today, CancellationToken.None);
        Assert.IsNotNull(view);
        Assert.IsFalse(view.ConfirmedVersionIsNotWorking);

        await context.Confirm.ExecuteAsync(
            context.Today,
            new Application.Reflections.ConfirmReflectionRequest(AcceptedUnsourcedClaims: true),
            CancellationToken.None);

        await context.SwitchVersion.ExecuteAsync(context.Today, second.Version.Id, CancellationToken.None);

        var afterSwitch = await context.GetReflection.ExecuteAsync(context.Today, CancellationToken.None);
        Assert.IsTrue(afterSwitch!.ConfirmedVersionIsNotWorking);
    }

    [TestMethod]
    public async Task The_sources_view_reports_an_unfinished_check_as_unfinished()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        var sources = await context.GetSources.ExecuteAsync(context.Today, CancellationToken.None);

        Assert.AreEqual(1, sources.Sources.Count);
        Assert.AreEqual(0, sources.UnsourcedClaims.Count);

        // The distinction that matters: no findings, and the check has not run. The client must be able to say so.
        Assert.IsNull(sources.CheckedAtUtc);
    }

    [TestMethod]
    public async Task A_citation_the_agent_could_not_place_is_dropped_and_counted()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        context.Client.Override = _ => new GeneratedDraft(
            "标题",
            "摘要",
            "只有这一段。",
            [],
            [],
            [
                // A sentence that is not in the body: the offsets cannot be derived, so it is dropped rather than
                // pointed at the wrong place (decision A.6).
                new GeneratedCitation("这句话根本不在正文里。", [InputEntryId.New()], 0.9, "编造的引用"),
            ],
            [],
            []);

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        var result = await context.Generate.ExecuteAsync(
            context.Today,
            ReflectionGenerationPayload.FromJson(request.Job!.Payload),
            CancellationToken.None);

        Assert.AreEqual(1, result.UnresolvedCitations);

        var version = await context.Reflections.FindVersionAsync(result.Version!.Id, CancellationToken.None);
        Assert.AreEqual(0, version!.Sources.Count);
    }

    [TestMethod]
    public async Task A_generation_failure_marks_the_draft_so_the_user_can_see_it()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        await context.CaptureTextAsync("今天试着记录了一点东西。");

        var request = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        context.Client.Override = _ => throw new PermanentExternalFailureException(
            "generation.request_rejected",
            "Rejected.");

        var handler = new Jobs.ReflectionGenerationJobHandler(
            context.Generate,
            context.Reflections,
            context.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jobs.ReflectionGenerationJobHandler>.Instance);

        await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(async () =>
            await handler.ExecuteAsync(request.Job!, CancellationToken.None));

        var reflection = await context.Reflections.FindByContentDateAsync(context.Today, CancellationToken.None);
        Assert.AreEqual(ReflectionStatus.Failed, reflection!.Status);

        // §14: 用户修复配置后可以重试单项. A failed draft is reachable by an explicit request.
        var retry = await context.RequestGeneration.ExecuteAsync(
            context.Today, manual: true, ignoreTranscriptionFailures: false, allowOverwriteOfManualEdits: false, CancellationToken.None);

        Assert.IsTrue(retry.Decision.Allowed, retry.Decision.Detail);
    }
}
