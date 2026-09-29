using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §8.4 与附录 A.24、A.28：写作规范是用户拥有的配置，不是一组固定枚举；
/// 篇幅是一个区间加一个可选公差，而不是一个目标值。
/// </summary>
[TestClass]
public sealed class WritingSettingsTests
{
    [TestMethod]
    public void The_default_spec_is_fifty_to_three_hundred_characters_with_twenty_of_slack()
    {
        var settings = WritingSettings.Default;

        Assert.AreEqual(50, settings.MinCharacters);
        Assert.AreEqual(300, settings.MaxCharacters);
        Assert.AreEqual(20, settings.CharacterTolerance);
        Assert.IsTrue(settings.AllowsTolerance);
        Assert.AreEqual(30, settings.ToleratedMinCharacters, "有公差时交给模型的下限是 50-20。");
        Assert.AreEqual(320, settings.ToleratedMaxCharacters, "有公差时交给模型的上限是 300+20。");
        Assert.AreEqual(WritingPerson.First, settings.Person);

        // 开箱即用的清单不能是空的：用户第一次打开页面时应当看到一份可以改的东西，而不是一片空白。
        Assert.IsTrue(settings.Rules.Count > 0, "默认规范清单不该是空的。");
        Assert.AreEqual("行文风格", settings.Rules[0].Title, "第一条就是用户点名要的「博客行文风格」。");
    }

    [TestMethod]
    public void A_spec_without_rules_is_valid_because_deleting_every_rule_is_a_choice()
    {
        var settings = new WritingSettings(50, 300, 20, WritingPerson.First, []);

        settings.Validate();

        Assert.AreEqual(0, settings.Rules.Count);
    }

    [TestMethod]
    public void A_null_rule_list_becomes_empty_instead_of_staying_null()
    {
        // 反序列化一个缺字段的旧 blob 会走到这里；留着 null 会在第一次枚举时炸掉，而不是变成「没有规范」。
        var settings = new WritingSettings(50, 300, 20, WritingPerson.First, null);

        Assert.IsNotNull(settings.Rules);
        Assert.AreEqual(0, settings.Rules.Count);
    }

    /// <summary>关掉公差就是把区间说死：0 是合法值，此时交给模型的上下限就是区间本身。</summary>
    [TestMethod]
    public void A_zero_tolerance_means_the_range_is_strict()
    {
        var settings = new WritingSettings(80, 200, 0, WritingPerson.First);

        settings.Validate();

        Assert.IsFalse(settings.AllowsTolerance);
        Assert.AreEqual(80, settings.ToleratedMinCharacters);
        Assert.AreEqual(200, settings.ToleratedMaxCharacters);
    }

    /// <summary>公差不能把下限推到 0 以下：交给模型的字数下限至少是 1。</summary>
    [TestMethod]
    public void A_tolerance_larger_than_the_minimum_never_produces_a_non_positive_lower_bound()
    {
        var settings = new WritingSettings(10, 300, 25, WritingPerson.First);

        settings.Validate();

        Assert.AreEqual(1, settings.ToleratedMinCharacters);
    }

    [TestMethod]
    public void Implausible_bounds_are_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.min.out_of_range",
            () => new WritingSettings(0, 300, 20, WritingPerson.First).Validate());

        TestFactory.ThrowsDomain(
            "writing.max.out_of_range",
            () => new WritingSettings(50, WritingSettings.MaxAllowedCharacters + 1, 20, WritingPerson.First).Validate());
    }

    /// <summary>最少必须小于最多——用户点名要的这条规则（相等的区间没有意义）。</summary>
    [TestMethod]
    public void A_minimum_that_is_not_below_the_maximum_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.range.invalid",
            () => new WritingSettings(300, 300, 20, WritingPerson.First).Validate());

        TestFactory.ThrowsDomain(
            "writing.range.invalid",
            () => new WritingSettings(400, 300, 20, WritingPerson.First).Validate());
    }

    [TestMethod]
    public void A_tolerance_outside_its_bounds_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.tolerance.out_of_range",
            () => new WritingSettings(50, 300, -1, WritingPerson.First).Validate());

        TestFactory.ThrowsDomain(
            "writing.tolerance.out_of_range",
            () => new WritingSettings(50, 300, WritingSettings.MaxAllowedTolerance + 1, WritingPerson.First).Validate());
    }

    [TestMethod]
    public void A_rule_needs_both_a_title_and_an_instruction()
    {
        TestFactory.ThrowsDomain(
            "writing.rule.title_invalid",
            () => new WritingSettings(50, 300, 20, WritingPerson.First, [new WritingRule("   ", "有内容")]).Validate());

        TestFactory.ThrowsDomain(
            "writing.rule.instruction_invalid",
            () => new WritingSettings(50, 300, 20, WritingPerson.First, [new WritingRule("标题", "  ")]).Validate());
    }

    [TestMethod]
    public void A_rule_title_or_instruction_beyond_its_limit_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.rule.title_invalid",
            () => new WritingSettings(
                50,
                300,
                20,
                WritingPerson.First,
                [new WritingRule(new string('题', WritingRule.MaxTitleLength + 1), "有内容")]).Validate());

        TestFactory.ThrowsDomain(
            "writing.rule.instruction_invalid",
            () => new WritingSettings(
                50,
                300,
                20,
                WritingPerson.First,
                [new WritingRule("标题", new string('字', WritingRule.MaxInstructionLength + 1))]).Validate());
    }

    [TestMethod]
    public void Too_many_rules_are_rejected()
    {
        var rules = Enumerable
            .Range(0, WritingSettings.MaxRules + 1)
            .Select(index => new WritingRule($"第 {index} 条", "内容"))
            .ToArray();

        TestFactory.ThrowsDomain(
            "writing.rules.too_many",
            () => new WritingSettings(50, 300, 20, WritingPerson.First, rules).Validate());
    }
}
