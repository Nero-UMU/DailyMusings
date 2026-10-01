using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// 写作规范在设置表里的往返（docs/开发指导.md §8.4，附录 A.24）。
/// <para>
/// 这里要守住的是「默认值只在从未配置过时出现」这一条：用户把清单删空是一次明确的选择，读回来时又被塞回
/// 默认条目，等于界面上的删除按钮没有用。
/// </para>
/// </summary>
[TestClass]
public sealed class WritingSpecSettingsTests
{
    [TestMethod]
    public void An_instance_that_never_saved_a_spec_gets_the_default_rules()
    {
        var settings = ContentSettings.FromValues(new Dictionary<string, string>());

        Assert.AreEqual(WritingSettings.DefaultMinCharacters, settings.Writing.MinCharacters);
        Assert.AreEqual(WritingSettings.DefaultMaxCharacters, settings.Writing.MaxCharacters);
        Assert.AreEqual(WritingSettings.DefaultCharacterTolerance, settings.Writing.CharacterTolerance);
        Assert.AreEqual(WritingPerson.First, settings.Writing.Person);
        Assert.AreEqual(WritingSettings.DefaultRules.Count, settings.Writing.Rules.Count);
    }

    [TestMethod]
    public void A_spec_the_user_emptied_stays_empty_instead_of_being_reseeded()
    {
        var saved = ContentSettings.Default with
        {
            Writing = new WritingSettings(120, 420, 30, WritingPerson.Third, []),
        };

        var reloaded = ContentSettings.FromValues(saved.ToValues());

        Assert.AreEqual(120, reloaded.Writing.MinCharacters);
        Assert.AreEqual(420, reloaded.Writing.MaxCharacters);
        Assert.AreEqual(30, reloaded.Writing.CharacterTolerance);
        Assert.AreEqual(WritingPerson.Third, reloaded.Writing.Person);
        Assert.AreEqual(
            0,
            reloaded.Writing.Rules.Count,
            "用户把规范清单删空是明确的决定，读回来时不能再变回默认条目。");
    }

    [TestMethod]
    public void The_rules_survive_a_round_trip_in_the_order_the_user_arranged_them()
    {
        var saved = ContentSettings.Default with
        {
            Writing = new WritingSettings(
                80,
                300,
                0,
                WritingPerson.Second,
                [
                    new WritingRule("第一条", "内容一"),
                    new WritingRule("第二条", "内容二"),
                ]),
        };

        var reloaded = ContentSettings.FromValues(saved.ToValues());

        Assert.AreEqual(2, reloaded.Writing.Rules.Count);
        Assert.AreEqual("第一条", reloaded.Writing.Rules[0].Title);
        Assert.AreEqual("内容一", reloaded.Writing.Rules[0].Instruction);
        Assert.AreEqual("第二条", reloaded.Writing.Rules[1].Title);
    }

    [TestMethod]
    public void An_unreadable_rules_blob_falls_back_to_the_defaults()
    {
        var values = new Dictionary<string, string>(ContentSettings.Default.ToValues())
        {
            [ContentSettings.WritingRulesKey] = "{ 这不是 JSON",
        };

        var reloaded = ContentSettings.FromValues(values);

        Assert.AreEqual(
            WritingSettings.DefaultRules.Count,
            reloaded.Writing.Rules.Count,
            "读不懂的规范不是规范；退回文档写的默认值，总好过让一次生成失败。");
    }

    /// <summary>
    /// 兼容路径（A.28）：已经存过单一目标字数的实例，读到新界面时不该被打回默认值——旧值当作**最多字数**，
    /// 于是一个写着 300 的实例开箱就是「最少 50、最多 300」。
    /// </summary>
    [TestMethod]
    public void An_instance_that_saved_the_old_single_target_keeps_it_as_the_maximum()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContentSettings.WritingTargetCharactersKey] = "450",
        };

        var reloaded = ContentSettings.FromValues(values);

        Assert.AreEqual(450, reloaded.Writing.MaxCharacters, "旧的 450 变成「最多 450 字」。");
        Assert.AreEqual(
            WritingSettings.DefaultMinCharacters,
            reloaded.Writing.MinCharacters,
            "旧实例没有区间，下限用默认值。");
    }

    [TestMethod]
    public void The_person_is_stored_as_a_readable_name_not_an_ordinal()
    {
        // 存序号的话，以后往枚举中间插一个值就会把既有实例的人称整体挪位。
        var values = ContentSettings.Default.ToValues();

        Assert.AreEqual("first", values[ContentSettings.WritingPersonKey]);
        Assert.AreEqual("50", values[ContentSettings.WritingMinCharactersKey]);
        Assert.AreEqual("300", values[ContentSettings.WritingMaxCharactersKey]);
        Assert.AreEqual("20", values[ContentSettings.WritingToleranceKey]);
        Assert.AreEqual("7", values[ContentSettings.WritingRecentArticleDaysKey], "成稿窗口默认 7 天，落库为可读的数字。");
        Assert.IsTrue(ContentSettings.TryParsePerson("Third", out var person));
        Assert.AreEqual(WritingPerson.Third, person, "解析要么写死小写，要么大小写无关；这里是后者。");
        Assert.IsFalse(ContentSettings.TryParsePerson("fourth", out _));
    }
}
