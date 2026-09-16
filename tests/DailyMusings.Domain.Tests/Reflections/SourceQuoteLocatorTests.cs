using DailyMusings.Domain.Reflections.Sources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §6.5 and decision A.6. The source map's offsets are derived from the draft text here
/// rather than taken from the model, because a citation that highlights the wrong sentence is worse than one
/// that admits it could not be placed.
/// </summary>
[TestClass]
public class SourceQuoteLocatorTests
{
    private const string Body = "第一段的内容。\n\n第二段内容，说的是录音上传。\n\n第三段收尾。";

    [TestMethod]
    public void A_quote_is_located_in_its_own_paragraph()
    {
        var located = SourceQuoteLocator.Locate(Body, "第二段内容");

        Assert.IsNotNull(located);
        Assert.AreEqual(1, located.BlockIndex);
        Assert.AreEqual(0, located.CharStart);
        Assert.AreEqual(5, located.CharEnd);
        Assert.AreEqual("第二段内容", located.Text);
    }

    [TestMethod]
    public void A_quote_later_in_a_paragraph_gets_the_right_range()
    {
        var located = SourceQuoteLocator.Locate(Body, "录音上传");

        Assert.IsNotNull(located);
        Assert.AreEqual(1, located.BlockIndex);
        Assert.AreEqual(9, located.CharStart, "第二段内容，说的是 is nine characters long.");
        Assert.AreEqual(13, located.CharEnd);
    }

    [TestMethod]
    public void A_quote_that_does_not_appear_is_reported_as_unresolved()
    {
        // Never approximated: callers drop it, and the missing citation is visible instead of misleading.
        Assert.IsNull(SourceQuoteLocator.Locate(Body, "这段文字根本不存在"));
    }

    [TestMethod]
    public void Re_wrapped_whitespace_does_not_lose_the_quote()
    {
        var body = "今天 试了\n一下录音。";
        var located = SourceQuoteLocator.Locate(body, "今天试了一下录音");

        Assert.IsNotNull(located);
        Assert.AreEqual(0, located.BlockIndex);
        Assert.AreEqual(0, located.CharStart);
        Assert.AreEqual(body.TrimEnd('。').Length, located.CharEnd);
    }

    [TestMethod]
    public void The_located_text_is_the_drafts_own_text_not_the_models_rendering()
    {
        var body = "今天  试了录音。";
        var located = SourceQuoteLocator.Locate(body, "今天 试了录音");

        Assert.IsNotNull(located);
        Assert.AreEqual("今天  试了录音", located.Text, "The range must cover the real double space.");
    }

    [TestMethod]
    public void An_empty_body_or_quote_is_not_located()
    {
        Assert.IsNull(SourceQuoteLocator.Locate(null, "内容"));
        Assert.IsNull(SourceQuoteLocator.Locate(Body, null));
        Assert.IsNull(SourceQuoteLocator.Locate(Body, "   "));
        Assert.IsNull(SourceQuoteLocator.Locate("   ", "内容"));
    }

    [TestMethod]
    public void A_whole_paragraph_can_be_quoted()
    {
        var located = SourceQuoteLocator.Locate(Body, "第三段收尾。");

        Assert.IsNotNull(located);
        Assert.AreEqual(2, located.BlockIndex);
        Assert.AreEqual(0, located.CharStart);
        Assert.AreEqual("第三段收尾。".Length, located.CharEnd);
    }

    [TestMethod]
    public void Locating_a_quote_that_spans_two_paragraphs_fails_rather_than_guessing()
    {
        // §6.5 locates inside one paragraph. A quote that spans a paragraph break cannot be expressed as a
        // single range, so it is reported unresolved instead of being truncated to one side.
        Assert.IsNull(SourceQuoteLocator.Locate(Body, "第一段的内容。\n\n第二段内容"));
    }
}
