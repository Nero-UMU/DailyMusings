using DailyMusings.Domain.Retrieval;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Retrieval;

/// <summary>
/// The similarity measure behind degraded retrieval (§8.3) and automatic topic recognition (§8.2). It is a
/// pure function on purpose: everything downstream of it — which material a prompt sees, which topic an input
/// is filed under — is only reproducible if the scoring is.
/// </summary>
[TestClass]
public class TextSimilarityTests
{
    [TestMethod]
    public void Chinese_text_without_spaces_still_shares_terms()
    {
        var score = TextSimilarity.Jaccard("今天试了一下录音和上传", "录音上传又失败了");

        Assert.IsTrue(score > 0, "Character bigrams must find the shared 录音/上传 without any segmentation.");
    }

    [TestMethod]
    public void Unrelated_text_scores_zero()
    {
        Assert.AreEqual(0.0, TextSimilarity.Jaccard("今天试了一下录音", "明天要去爬山看日出"));
    }

    [TestMethod]
    public void Empty_input_scores_zero_rather_than_one()
    {
        Assert.AreEqual(0.0, TextSimilarity.Jaccard(null, "有内容"));
        Assert.AreEqual(0.0, TextSimilarity.Jaccard(string.Empty, string.Empty));
        Assert.AreEqual(0.0, TextSimilarity.Coverage("", "有内容"));
    }

    [TestMethod]
    public void Coverage_rewards_a_short_query_inside_a_long_document()
    {
        // Jaccard is symmetric and therefore punishes length differences, which is the wrong question when
        // asking "does this long past note talk about the two words I just used?".
        var jaccard = TextSimilarity.Jaccard("录音", "关于录音这件事我写了很长的内容来说明当时的情况和感受");
        var coverage = TextSimilarity.Coverage("录音", "关于录音这件事我写了很长的内容来说明当时的情况和感受");

        Assert.IsTrue(coverage > jaccard, $"Coverage ({coverage}) must exceed Jaccard ({jaccard}).");
        Assert.AreEqual(1.0, coverage, 0.0001, "The whole query is present, so coverage is complete.");
    }

    [TestMethod]
    public void Latin_words_match_regardless_of_case()
    {
        Assert.AreEqual(1.0, TextSimilarity.Jaccard("Hexo Markdown", "hexo markdown"));
    }

    [TestMethod]
    public void Punctuation_and_spacing_do_not_change_the_terms()
    {
        CollectionAssert.AreEquivalent(
            TextSimilarity.Tokenize("录音、上传").ToArray(),
            TextSimilarity.Tokenize("录音 上传").ToArray());
    }

    [TestMethod]
    public void A_single_character_is_its_own_term()
    {
        // A one-character note has no bigram; dropping it entirely would make it unmatchable.
        CollectionAssert.AreEquivalent(new[] { "累" }, TextSimilarity.Tokenize("累").ToArray());
    }
}
