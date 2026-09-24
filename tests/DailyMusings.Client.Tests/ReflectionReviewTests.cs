using DailyMusings.Client.Core.Reflections;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The draft screen's rules (docs/开发指导.md §6.3, §6.4, §8.4, §9.3, §11.1). They are pure functions over the API's
/// DTOs precisely so that the promises they carry can be asserted without a device: a hand edit is never overwritten
/// without an answer, an untraced sentence is never confirmed silently, and a citation that no longer resolves is not
/// drawn as if it did.
/// </summary>
[TestClass]
public class ReflectionReviewTests
{
    private static SourceReferenceDto Source(
        string inputId = "input-1",
        int blockIndex = 0,
        int start = 0,
        int end = 5,
        bool historical = false,
        string drift = SourceDriftNames.Exact) =>
        new(inputId, blockIndex, start, end, 0.9, "与素材一致", historical, drift);

    private static ReflectionVersionDto Version(
        string id = "version-1",
        string body = "第一段。\n\n第二段。",
        bool manualEdits = false,
        string? checkedAt = "2026-03-01T20:00:00Z",
        IReadOnlyList<SourceReferenceDto>? sources = null,
        IReadOnlyList<UnsourcedClaimDto>? claims = null) =>
        new(
            id,
            "标题",
            "摘要",
            body,
            ["随想"],
            ["日记"],
            manualEdits,
            "2026-03-01T19:00:00Z",
            manualEdits ? "2026-03-01T19:30:00Z" : null,
            "stub-chat",
            "v1",
            checkedAt,
            sources ?? [],
            claims ?? []);

    private static ReflectionDto Reflection(
        string status = ReflectionStatusNames.ReviewRequired,
        ReflectionVersionDto? working = null,
        string? lastStaleReason = null,
        string? confirmedVersionId = null,
        bool confirmedIsNotWorking = false,
        SemanticSearchDto? semantic = null)
    {
        var version = working ?? Version();

        return new ReflectionDto(
            "reflection-1",
            "2026-03-01",
            status,
            GenerationReasonNames.Manual,
            lastStaleReason,
            version.Id,
            null,
            version.Id,
            confirmedVersionId,
            confirmedIsNotWorking,
            "2026-03-01T19:00:00Z",
            "2026-03-01T19:30:00Z",
            version,
            null,
            version,
            semantic ?? new SemanticSearchDto(true, true, false));
    }

    [TestMethod]
    public void Confirming_only_needs_an_acknowledgement_when_the_check_found_something()
    {
        Assert.IsFalse(ReflectionReview.ConfirmNeedsAcknowledgement(Version(claims: [])));

        Assert.IsTrue(ReflectionReview.ConfirmNeedsAcknowledgement(
            Version(claims: [new UnsourcedClaimDto(1, 0, 6, "素材里没有这件事")])));

        Assert.IsFalse(ReflectionReview.ConfirmNeedsAcknowledgement(null));
    }

    [TestMethod]
    public void Regenerating_only_needs_a_warning_when_the_working_version_was_edited_by_hand()
    {
        Assert.IsFalse(ReflectionReview.RegenerateNeedsOverwriteConfirmation(Reflection()));
        Assert.IsTrue(ReflectionReview.RegenerateNeedsOverwriteConfirmation(Reflection(working: Version(manualEdits: true))));
        Assert.IsFalse(ReflectionReview.RegenerateNeedsOverwriteConfirmation(null));
    }

    [TestMethod]
    public void The_warnings_name_every_state_the_guide_says_the_user_must_be_told_about()
    {
        var reflection = Reflection(
            status: ReflectionStatusNames.StaleByLateInput,
            working: Version(
                manualEdits: true,
                checkedAt: null,
                sources: [Source(drift: SourceDriftNames.Drifted)]),
            lastStaleReason: "晚上又录了一条",
            confirmedVersionId: "version-0",
            confirmedIsNotWorking: true,
            semantic: new SemanticSearchDto(true, false, true));

        var warnings = ReflectionReview.DescribeWarnings(reflection);
        var text = string.Join(" | ", warnings);

        StringAssert.Contains(text, "新的输入");
        StringAssert.Contains(text, "晚上又录了一条");
        StringAssert.Contains(text, "手工修改");
        StringAssert.Contains(text, "来源检查还没完成");
        StringAssert.Contains(text, "引用");
        StringAssert.Contains(text, "已确认的版本不是当前版本");
        StringAssert.Contains(text, "语义检索重建中");
    }

    [TestMethod]
    public void A_draft_that_needs_nothing_says_nothing()
    {
        Assert.AreEqual(0, ReflectionReview.DescribeWarnings(Reflection()).Count);
        Assert.AreEqual(0, ReflectionReview.DescribeWarnings(null).Count);
    }

    [TestMethod]
    public void A_citation_is_sliced_out_of_the_body_it_was_computed_against()
    {
        var body = "今天早上晨跑。\n\n晚上写了一段代码。";

        Assert.AreEqual("今天早上晨跑。", ReflectionReview.SliceQuote(body, 0, 0, 7));
        Assert.AreEqual("晚上写了一段", ReflectionReview.SliceQuote(body, 1, 0, 6));

        // A drifted paragraph still has text to show, and an offset that no longer fits is clamped rather than
        // throwing on a screen that is only trying to draw a list.
        Assert.AreEqual("晚上写了一段代码。", ReflectionReview.BlockAt(body, 1));
        Assert.AreEqual(string.Empty, ReflectionReview.BlockAt(body, 9));
        Assert.AreEqual(string.Empty, ReflectionReview.SliceQuote(body, 9, 0, 3));
        Assert.AreEqual("今天早上晨跑。", ReflectionReview.SliceQuote(body, 0, 0, 999));
    }

    [TestMethod]
    public void The_same_blank_line_split_is_used_on_both_sides()
    {
        var blocks = ReflectionReview.SplitBlocks("一。\n\n二。\r\n\r\n三。");

        Assert.AreEqual(3, blocks.Count);
        Assert.AreEqual("一。", blocks[0]);
        Assert.AreEqual("三。", blocks[2]);
        Assert.AreEqual(0, ReflectionReview.SplitBlocks(null).Count);
    }

    [TestMethod]
    public void A_version_is_named_by_the_slot_it_sits_in()
    {
        var reflection = Reflection(confirmedVersionId: "version-2");

        Assert.AreEqual("当前版本", ReflectionReview.DescribeVersionSlot(reflection, "version-1"));
        Assert.AreEqual("已确认版本", ReflectionReview.DescribeVersionSlot(reflection, "version-2"));
        Assert.AreEqual("某一版", ReflectionReview.DescribeVersionSlot(reflection, "version-9"));
    }

    [TestMethod]
    public void A_publication_reads_as_target_origin_and_state()
    {
        var publication = new PublicationDto(
            "publication-1",
            "reflection-1",
            "version-1",
            "target-1",
            "Hexo 输出",
            PublishTargetTypeNames.Markdown,
            PublicationTriggerNames.Manual,
            PublicationStatusNames.DraftUploaded,
            PublicationVisibilityNames.Draft,
            "2026-03-01-标题.md",
            1,
            "2026-03-01T20:00:00Z",
            "device:1",
            "2026-03-01T20:00:01Z",
            "2026-03-01T20:00:02Z",
            null,
            null,
            false,
            false,
            false,
            0);

        var text = ReflectionReview.DescribePublication(publication);

        StringAssert.Contains(text, "Hexo 输出");
        StringAssert.Contains(text, "手动草稿");

        // The export is a file on disk, so calling it an upload would describe an action that never happened.
        StringAssert.Contains(text, "已写入 Markdown 文件");
    }

    [TestMethod]
    public void The_servers_own_refusal_codes_become_sentences_the_user_can_act_on()
    {
        StringAssert.Contains(
            ReflectionReview.DescribeConfirmRefusal("reflection.confirm.unsourced_claims_not_acknowledged"),
            "无法追溯");
        StringAssert.Contains(
            ReflectionReview.DescribeGenerationRefusal("reflection.regeneration.date_not_current"),
            "只能重新生成今天");
        StringAssert.Contains(
            ReflectionReview.DescribeGenerationRefusal("reflection.regeneration.overwrites_manual_edits"),
            "手工修改");
        StringAssert.Contains(
            ReflectionReview.DescribePublishRefusal("publication.reflection_not_confirmed"),
            "还没确认");
        StringAssert.Contains(
            ReflectionReview.DescribePublishRefusal("publication.window_expired"),
            "超过执行窗口");
        StringAssert.Contains(
            ReflectionReview.DescribeTransportFailure("auth.device_token_rejected"),
            "重新配对");
        StringAssert.Contains(
            ReflectionReview.DescribeTransportFailure("client.not_configured"),
            "配置服务器地址");
        StringAssert.Contains(
            ReflectionReview.DescribeTransportFailure("server.rejected.500"),
            "内部出错");
    }

    /// <summary>
    /// A day nobody has generated yet answers 404 with the server's own code, and that is not a network failure: it is
    /// the normal state of every new day, and the first thing the user sees.
    /// </summary>
    [TestMethod]
    public void A_day_without_a_draft_is_not_reported_as_a_network_failure()
    {
        Assert.IsTrue(ReflectionReview.IsMissingDraft("request.not_found"));
        Assert.IsTrue(ReflectionReview.IsMissingDraft("server.rejected.404"));
        Assert.IsFalse(ReflectionReview.IsMissingDraft("client.network_unreachable"));
        Assert.IsFalse(ReflectionReview.IsMissingDraft(null));

        StringAssert.Contains(
            ReflectionReview.DescribeTransportFailure("request.not_found"),
            "还没有这一天的草稿");

        // An unknown code is shown verbatim rather than replaced by a vague sentence that hides what happened.
        StringAssert.Contains(ReflectionReview.DescribeGenerationRefusal("something.new"), "something.new");
    }
}
