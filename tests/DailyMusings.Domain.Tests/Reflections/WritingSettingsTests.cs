using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §8.4 与附录 A.24：写作规范是用户拥有的配置，不是一组固定枚举。
/// </summary>
[TestClass]
public sealed class WritingSettingsTests
{
    [TestMethod]
    public void The_default_spec_is_three_hundred_characters_in_the_first_person()
    {
        var settings = WritingSettings.Default;

        Assert.AreEqual(300, settings.TargetCharacters);
        Assert.AreEqual(WritingPerson.First, settings.Person);

        // 开箱即用的清单不能是空的：用户第一次打开页面时应当看到一份可以改的东西，而不是一片空白。
        Assert.IsTrue(settings.Rules.Count > 0, "默认规范清单不该是空的。");
        Assert.AreEqual("行文风格", settings.Rules[0].Title, "第一条就是用户点名要的「博客行文风格」。");
    }

    [TestMethod]
    public void A_spec_without_rules_is_valid_because_deleting_every_rule_is_a_choice()
    {
        var settings = new WritingSettings(300, WritingPerson.First, []);

        settings.Validate();

        Assert.AreEqual(0, settings.Rules.Count);
    }

    [TestMethod]
    public void A_null_rule_list_becomes_empty_instead_of_staying_null()
    {
        // 反序列化一个缺字段的旧 blob 会走到这里；留着 null 会在第一次枚举时炸掉，而不是变成「没有规范」。
        var settings = new WritingSettings(300, WritingPerson.First, null);

        Assert.IsNotNull(settings.Rules);
        Assert.AreEqual(0, settings.Rules.Count);
    }

    [TestMethod]
    public void An_implausible_length_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.length.out_of_range",
            () => new WritingSettings(0, WritingPerson.First).Validate());

        TestFactory.ThrowsDomain(
            "writing.length.out_of_range",
            () => new WritingSettings(WritingSettings.MaxTargetCharacters + 1, WritingPerson.First).Validate());
    }

    [TestMethod]
    public void A_rule_needs_both_a_title_and_an_instruction()
    {
        TestFactory.ThrowsDomain(
            "writing.rule.title_invalid",
            () => new WritingSettings(300, WritingPerson.First, [new WritingRule("   ", "有内容")]).Validate());

        TestFactory.ThrowsDomain(
            "writing.rule.instruction_invalid",
            () => new WritingSettings(300, WritingPerson.First, [new WritingRule("标题", "  ")]).Validate());
    }

    [TestMethod]
    public void A_rule_title_or_instruction_beyond_its_limit_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "writing.rule.title_invalid",
            () => new WritingSettings(
                300,
                WritingPerson.First,
                [new WritingRule(new string('题', WritingRule.MaxTitleLength + 1), "有内容")]).Validate());

        TestFactory.ThrowsDomain(
            "writing.rule.instruction_invalid",
            () => new WritingSettings(
                300,
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
            () => new WritingSettings(300, WritingPerson.First, rules).Validate());
    }
}
