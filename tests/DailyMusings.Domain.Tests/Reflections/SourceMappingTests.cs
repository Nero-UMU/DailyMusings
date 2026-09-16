using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §6.5, §8.4 and decision A.6. A source map that points at the wrong sentence is
/// worse than one that admits uncertainty, so drift must be detected rather than ignored.
/// </summary>
[TestClass]
public class SourceMappingTests
{
    private const string Body = "第一段内容。\n\n第二段内容。";

    [TestMethod]
    public void An_untouched_paragraph_still_resolves_exactly()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection, Body);
        var source = TestFactory.NewSource(version, blockIndex: 0, charStart: 0, charEnd: 3, quotedText: "第一段");
        version.AttachSources([source]);

        var result = version.CheckSourceDrift().Single();

        Assert.AreEqual(SourceDrift.Exact, result.Drift);
    }

    [TestMethod]
    public void Editing_a_paragraph_clears_the_map_so_nothing_can_be_mis_highlighted()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection, Body);
        var source = TestFactory.NewSource(version, 0, 0, 3, "第一段");
        version.AttachSources([source]);

        version.Edit("标题", "摘要", "改写后的开头。\n\n第二段内容。", TestFactory.Noon);

        // Editing drops the map outright: there is deliberately nothing left that could point at the
        // wrong sentence. Re-attaching the old map is what the detector guards against.
        Assert.AreEqual(0, version.Sources.Count);

        version.AttachSources([source]);

        Assert.AreEqual(SourceDrift.Drifted, version.CheckSourceDrift().Single().Drift);
    }

    [TestMethod]
    public void A_stale_map_against_rewritten_text_reports_drift()
    {
        var reflection = TestFactory.NewReflection();
        var original = TestFactory.NewVersion(reflection, Body);
        var source = TestFactory.NewSource(original, 0, 0, 3, "第一段");
        original.AttachSources([source]);

        // Simulate the real hazard: the body changed but the recorded hash did not.
        var edited = ReflectionVersion.Rehydrate(
            original.Id,
            reflection.Id,
            "标题",
            "摘要",
            "完全换掉的句子。\n\n第二段内容。",
            WritingSettings.Default,
            null,
            null,
            hasManualEdits: true,
            TestFactory.Noon,
            TestFactory.Noon);
        edited.AttachSources([source]);

        Assert.AreEqual(SourceDrift.Drifted, edited.CheckSourceDrift().Single().Drift);
    }

    [TestMethod]
    public void A_paragraph_that_no_longer_exists_is_unresolvable()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection, Body);
        var source = TestFactory.NewSource(version, blockIndex: 5, charStart: 0, charEnd: 1, quotedText: "第");
        version.AttachSources([source]);

        Assert.AreEqual(SourceDrift.Unresolvable, version.CheckSourceDrift().Single().Drift);
    }

    [TestMethod]
    public void A_range_beyond_the_paragraph_is_treated_as_drifted()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection, Body);
        var source = TestFactory.NewSource(version, 0, 40, 50, "不存在的片段");
        version.AttachSources([source]);

        Assert.AreEqual(SourceDrift.Drifted, version.CheckSourceDrift().Single().Drift);
    }

    [TestMethod]
    public void Rewrapping_whitespace_and_case_is_not_drift()
    {
        // Re-flowing a paragraph must not be reported as a content change; a wording change must be.
        Assert.IsTrue(SourceQuoteHash.Matches("今天  路过一条   小巷。", SourceQuoteHash.Compute("今天 路过一条 小巷。")));
        Assert.IsTrue(SourceQuoteHash.Matches("HELLO WORLD", SourceQuoteHash.Compute("hello world")));
        Assert.IsFalse(SourceQuoteHash.Matches("今天路过两条小巷。", SourceQuoteHash.Compute("今天路过一条小巷。")));
    }

    [TestMethod]
    public void A_source_cannot_be_attached_to_a_version_it_was_not_produced_for()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);
        var foreign = TestFactory.NewSource(TestFactory.NewVersion(reflection));

        TestFactory.ThrowsDomain(
            "reflection.version.source_from_other_version",
            () => version.AttachSources([foreign]));
    }

    [TestMethod]
    public void Source_reference_rejects_impossible_spans_and_relevance()
    {
        var version = TestFactory.NewVersion(TestFactory.NewReflection());

        TestFactory.ThrowsDomain(
            "source.range.invalid",
            () => TestFactory.NewSource(version, 0, charStart: 5, charEnd: 2));

        TestFactory.ThrowsDomain(
            "source.block_index.negative",
            () => TestFactory.NewSource(version, blockIndex: -1));

        TestFactory.ThrowsDomain(
            "source.relevance.out_of_range",
            () => SourceReference.Create(
                Domain.Common.SourceReferenceId.New(),
                version.Id,
                0,
                0,
                1,
                "x",
                Domain.Common.InputEntryId.New(),
                1.5,
                "r",
                false));
    }

    [TestMethod]
    public void Second_stage_findings_are_attached_as_warnings_not_blocks()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);

        version.AttachUnsourcedClaims([new UnsourcedClaim(0, 0, 5, "没有对应输入")], TestFactory.Noon);

        Assert.AreEqual(1, version.UnsourcedClaims.Count);
        Assert.AreEqual("没有对应输入", version.UnsourcedClaims[0].Reason);
    }

    [TestMethod]
    public void Revisions_are_marked_so_rotation_can_protect_them()
    {
        var reflection = TestFactory.NewReflection();
        var version = TestFactory.NewVersion(reflection);

        Assert.IsFalse(version.HasManualEdits);

        version.Edit("新标题", "新摘要", "新正文。", TestFactory.Noon);

        Assert.IsTrue(version.HasManualEdits);
        Assert.AreEqual(TestFactory.Noon, version.EditedAtUtc);
    }
}
