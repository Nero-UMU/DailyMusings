using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// 流水账判据（docs/开发指导.md 附录 A.41 续记三）。
/// <para>
/// 这一层只能在这里验：判据是纯计算，不看库也不看模型；而它判错了的代价很具体——误判会让一版本来合格的
/// 文章被无谓重写一遍，还要多花一次模型调用。所以正向用**线上真实出现过的那一篇**当回归用例，反向用
/// 用户选定的那个结构当反例。
/// </para>
/// </summary>
[TestClass]
public sealed class ArticleStructurePolicyTests
{
    private static readonly InputEntryId A = InputEntryId.New();
    private static readonly InputEntryId B = InputEntryId.New();
    private static readonly InputEntryId C = InputEntryId.New();
    private static readonly InputEntryId D = InputEntryId.New();

    private static GeneratedCitation Cite(string quote, params InputEntryId[] ids) =>
        new(quote, ids, 0.9, "测试用例");

    /// <summary>线上真实出现过的那一篇：四段对四条素材，正是用户报的流水账。</summary>
    [TestMethod]
    public void The_article_the_user_rejected_is_judged_an_inventory()
    {
        const string body = """
            凌晨一点多，我又改了一遍每日随想的代码。能跑是能跑，但离我想象里的项目还差几次修改。

            改完去找朋友玩洛克王国，两个人一起探图。他抓到一只非常稀有的精灵。昨天也是这样，一玩就到快五点。

            晚上睡得太晚，早上却很早就醒了——要去参加高中同学的婚礼。同学一个个都结婚了，我还在学校读研。

            中午的婚礼结束，下午又去了他家。几个高中同学坐在一起聊天，刚回到家，觉得时间过得好快。
            """;

        var assessment = ArticleStructurePolicy.Assess(
            body,
            [
                Cite("凌晨一点多，我又改了一遍每日随想的代码。", A),
                Cite("他抓到一只非常稀有的精灵。", B),
                Cite("晚上睡得太晚，早上却很早就醒了", C),
                Cite("中午的婚礼结束，下午又去了他家。", D),
            ],
            [A, B, C, D]);

        Assert.IsTrue(assessment.LooksLikeInventory, "四段对四条素材就是流水账，必须判出来。");
        Assert.AreEqual(4, assessment.Paragraphs);
        Assert.AreEqual(4, assessment.ParagraphsWithSources);
        Assert.AreEqual(4, assessment.SingleSourceParagraphs);
    }

    /// <summary>用户选定的结构：三段落，每段把两件事缝在一起——判据必须放过它。</summary>
    [TestMethod]
    public void The_structure_the_user_chose_is_not_judged_an_inventory()
    {
        const string body = """
            凌晨一点多，把每日随想的代码又改了一版，还是离我想象里的项目差几次修改，今天先到这里。顺手和朋友开了洛克王国，两个人分头探图，他抓到一只非常稀有的精灵，运气确实不错。

            真正让我停下来的不是这些。前一晚打游戏到两点多，睡得晚，早上却醒得很早，因为要去参加高中同学的婚礼。席上坐着的人一个个都成了家，我还在学校里读研。下午又去他家坐了坐，几个高中同学聊到傍晚，刚回到家。

            一天里最沉的其实不在代码里，也不在游戏里。
            """;

        var assessment = ArticleStructurePolicy.Assess(
            body,
            [
                Cite("凌晨一点多，把每日随想的代码又改了一版", A, B),
                Cite("真正让我停下来的不是这些。", C, D),
            ],
            [A, B, C, D]);

        Assert.IsFalse(
            assessment.LooksLikeInventory,
            "段落数少于素材数、且每段都不止一条素材，这正是要的形状，不该被判流水账。");
    }

    /// <summary>三条素材以下不判：两三件事写成两段本来就正常。</summary>
    [TestMethod]
    public void Two_material_days_are_never_judged()
    {
        var assessment = ArticleStructurePolicy.Assess(
            "第一件事。\n\n第二件事。",
            [Cite("第一件事。", A), Cite("第二件事。", B)],
            [A, B]);

        Assert.IsFalse(assessment.LooksLikeInventory);
    }

    /// <summary>找不到的引文按来源映射的既有规则丢掉，不能凭它把一段算成「有来源」。</summary>
    [TestMethod]
    public void Quotes_that_cannot_be_located_do_not_count()
    {
        var assessment = ArticleStructurePolicy.Assess(
            "第一件事。\n\n第二件事。\n\n第三件事。",
            [Cite("这句话不在正文里。", A, B, C)],
            [A, B, C]);

        Assert.IsFalse(assessment.LooksLikeInventory);
        Assert.AreEqual(0, assessment.ParagraphsWithSources);
    }
}
