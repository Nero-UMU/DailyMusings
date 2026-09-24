using DailyMusings.Domain.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Publishing;

/// <summary>
/// docs/开发指导.md §11.2. The Markdown export is the one place the product writes into a directory a person
/// also works in — their blog's source tree — so the interesting cases are all about not destroying anything.
/// </summary>
[TestClass]
public class MarkdownExportTests
{
    private static MarkdownDocument Document(
        string title = "今天的记录",
        string body = "第一段。\n\n第二段。",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? categories = null) =>
        new(
            title,
            "摘要",
            body,
            tags ?? ["记录", "录音"],
            categories ?? ["随想"],
            TestFactory.Day(10),
            TestFactory.Noon,
            IsDraft: true,
            Slug: MarkdownSlug.From(title));

    [TestMethod]
    public void The_default_template_produces_the_front_matter_the_guide_names()
    {
        var text = MarkdownTemplate.DefaultTemplate.Render(Document());

        StringAssert.Contains(text, "title: \"今天的记录\"");
        StringAssert.Contains(text, "date: 2026-03-10 00:00:00");
        StringAssert.Contains(text, "tags: [\"记录\", \"录音\"]");
        StringAssert.Contains(text, "categories: [\"随想\"]");
        StringAssert.Contains(text, "draft: true");
        StringAssert.Contains(text, "第一段。");
        Assert.IsTrue(text.StartsWith("---", StringComparison.Ordinal), "Front matter must open the file.");
    }

    [TestMethod]
    public void A_title_that_would_break_yaml_is_quoted_and_escaped()
    {
        var document = Document(title: "标题: \"带引号\" 与\n换行");

        var text = MarkdownTemplate.DefaultTemplate.Render(document);

        StringAssert.Contains(text, "title: \"标题: \\\"带引号\\\" 与\\n换行\"");

        // Nothing that could be read as a second YAML key leaked out of the value: the whole title sits inside
        // one quoted scalar, newline and all.
        var titleLine = text.Split('\n').First(line => line.StartsWith("title:", StringComparison.Ordinal));
        Assert.AreEqual("title: \"标题: \\\"带引号\\\" 与\\n换行\"", titleLine);
    }

    [TestMethod]
    public void An_unknown_placeholder_is_refused_rather_than_dropped()
    {
        var template = new MarkdownTemplate("---\ntitle: {titel}\n---\n\n{body}");

        var exception = Assert.ThrowsException<DailyMusings.Domain.Common.DomainException>(() => template.Render(Document()));

        // Silently dropping it would produce a file that looks fine and has lost a field.
        Assert.AreEqual("markdown.template.unknown_placeholder", exception.Code);
    }

    [TestMethod]
    public void A_template_without_the_body_placeholder_is_refused()
    {
        var template = new MarkdownTemplate("---\ntitle: {title}\n---\n");

        var exception = Assert.ThrowsException<DailyMusings.Domain.Common.DomainException>(() => template.Render(Document()));

        Assert.AreEqual("markdown.template.no_body", exception.Code);
    }

    [TestMethod]
    public void A_configured_template_with_extra_placeholders_still_renders()
    {
        var template = new MarkdownTemplate("---\ntitle: {title}\nslug: {slug}\nsummary: {summary}\n---\n\n{body}");

        var text = template.Render(Document(title: "Hexo 与 Markdown"));

        StringAssert.Contains(text, "slug: \"hexo-与-markdown\"");
        StringAssert.Contains(text, "summary: \"摘要\"");
    }

    [TestMethod]
    public void A_chinese_title_keeps_readable_characters_in_the_slug()
    {
        Assert.AreEqual("今天的记录", MarkdownSlug.From("今天的记录"));
        Assert.AreEqual("hexo-与-markdown", MarkdownSlug.From("Hexo 与 Markdown"));
    }

    [TestMethod]
    public void A_title_that_is_all_punctuation_yields_no_slug()
    {
        // The filename then falls back to the date alone, which is still a valid and unique name.
        Assert.AreEqual(string.Empty, MarkdownSlug.From("!!! ??? ..."));
        Assert.AreEqual("2026-03-10", MarkdownFileName.BaseName(TestFactory.Day(10), string.Empty));
    }

    [TestMethod]
    public void Path_separators_and_wildcards_never_reach_the_filename()
    {
        var slug = MarkdownSlug.From("a/b\\c:d*e?f");

        Assert.IsFalse(slug.Contains('/', StringComparison.Ordinal));
        Assert.IsFalse(slug.Contains('\\', StringComparison.Ordinal));
        Assert.IsFalse(slug.Contains(':', StringComparison.Ordinal));
        Assert.IsFalse(slug.Contains('*', StringComparison.Ordinal));
        Assert.IsFalse(slug.Contains('?', StringComparison.Ordinal));
    }

    [TestMethod]
    public void The_filename_is_the_content_day_plus_the_slug()
    {
        var name = MarkdownFileName.BaseName(TestFactory.Day(10), MarkdownSlug.From("今天的记录"));

        Assert.AreEqual("2026-03-10-今天的记录", name);
        Assert.AreEqual("2026-03-10-今天的记录.md", MarkdownFileName.NextVersionedName(name, _ => false));
    }

    [TestMethod]
    public void A_second_export_gets_a_versioned_name_rather_than_overwriting()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "2026-03-10-note.md" };

        var next = MarkdownFileName.NextVersionedName("2026-03-10-note", taken.Contains);

        Assert.AreEqual("2026-03-10-note-2.md", next);
        Assert.IsFalse(taken.Contains(next), "The returned name must be free by construction.");
    }

    [TestMethod]
    public void Versioned_names_keep_counting_up()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal)
        {
            "2026-03-10-note.md",
            "2026-03-10-note-2.md",
            "2026-03-10-note-3.md",
        };

        Assert.AreEqual("2026-03-10-note-4.md", MarkdownFileName.NextVersionedName("2026-03-10-note", taken.Contains));
    }

    [TestMethod]
    public void A_free_path_is_created_without_asking()
    {
        Assert.AreEqual(
            MarkdownWritePlan.CreateNewFile,
            MarkdownWritePolicy.Decide(fileExists: false, fileIsOurs: false, externallyModified: false, userConfirmedReplace: false));
    }

    [TestMethod]
    public void A_file_we_did_not_write_is_never_touched()
    {
        // Someone's own hand-written post may sit at exactly the name we would pick. Not overwriting it is the
        // whole of §11.2's promise — and no confirmation changes that, because the product cannot even identify
        // what it would be destroying.
        Assert.AreEqual(
            MarkdownWritePlan.RefuseUnowned,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: false, externallyModified: false, userConfirmedReplace: true));
    }

    /// <summary>
    /// The combination the writer actually produces for a hand-edited export: the recorded hash no longer
    /// matches, so the file is both "not ours" and "modified outside the product". Reporting the first instead
    /// of the second told the user the instance had never written that file, and made the
    /// <c>RefuseExternallyModified</c> plan unreachable.
    /// </summary>
    [TestMethod]
    public void A_file_edited_outside_the_product_is_reported_as_edited_not_as_unowned()
    {
        Assert.AreEqual(
            MarkdownWritePlan.RefuseExternallyModified,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: false, externallyModified: true, userConfirmedReplace: false));
    }

    /// <summary>
    /// §17.3 step 7: after the divergence has been reported, the user's choice has to be carried out. 覆盖 is
    /// only ever offered on a diverged export, so refusing it unconditionally made the action impossible to
    /// complete — the user could pick it and it would always fail with "we never wrote that file".
    /// </summary>
    [TestMethod]
    public void The_users_explicit_overwrite_carries_out_even_though_the_file_was_edited()
    {
        Assert.AreEqual(
            MarkdownWritePlan.ReplaceExistingFile,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: false, externallyModified: true, userConfirmedReplace: true));
    }

    [TestMethod]
    public void An_edited_file_is_kept_when_the_user_did_not_ask_to_replace_it()
    {
        // "保留两边" reaches the writer with the flag unset: the edited file stays and the export goes beside it.
        Assert.AreEqual(
            MarkdownWritePlan.RefuseExternallyModified,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: true, externallyModified: true, userConfirmedReplace: false));
    }

    [TestMethod]
    public void Our_own_untouched_file_is_replaced_only_when_the_user_asked()
    {
        Assert.AreEqual(
            MarkdownWritePlan.CreateNewFile,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: true, externallyModified: false, userConfirmedReplace: false));

        Assert.AreEqual(
            MarkdownWritePlan.ReplaceExistingFile,
            MarkdownWritePolicy.Decide(fileExists: true, fileIsOurs: true, externallyModified: false, userConfirmedReplace: true));
    }

    [TestMethod]
    public void The_document_uses_the_content_day_not_the_day_it_was_exported()
    {
        // §7's content day is when the material belongs; a post dated the export day would misreport when the
        // things in it happened, which is the same promise the recall boundary protects.
        var reflection = TestFactory.NewReflection(TestFactory.Day(10));
        var version = TestFactory.NewVersion(reflection);

        var document = MarkdownDocument.From(version, TestFactory.Day(10));

        Assert.AreEqual(TestFactory.Day(10), document.ContentDate);
        Assert.IsTrue(document.IsDraft, "An exported draft stays a draft until the user says otherwise.");
    }
}
