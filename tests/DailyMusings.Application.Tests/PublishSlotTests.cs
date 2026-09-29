using System.Globalization;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// 发布时刻的规则（docs/开发指导.md §11.1）：按**它出现在生成时刻之后的那一次**解释。
/// <para>
/// 这条规则是 2026-09-29 线上实例暴露出来的：生成 23:00、发布 23:16 这种再自然不过的配置，在旧实现里被
/// 无条件按「内容日期 +1 天」推到了 24 小时之后，于是「勾了自动发布到点却没动静」。三条路（调度、提醒、
/// 手动入队）都用 <see cref="ContentSettings.PublishSlotFor"/>，所以这里钉住的就是那一条。
/// </para>
/// </summary>
[TestClass]
public sealed class PublishSlotTests
{
    private static readonly ContentDate Day = ContentDate.From(new DateOnly(2026, 9, 29));

    private static ContentSettings Settings(string generation, string publish) => new(
        ContentSettings.DefaultTimeZoneId,
        TimeOnly.Parse(generation, CultureInfo.InvariantCulture),
        TimeOnly.Parse(publish, CultureInfo.InvariantCulture),
        ContentSettings.DefaultPublishWindowMinutes,
        ContentSettings.DefaultAudioRetentionDays);

    private static DateTimeOffset Local(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(8));

    /// <summary>默认组合：23:00 生成、次日 08:00 发布——25 小时里的第二天早上，行为不变。</summary>
    [TestMethod]
    public void A_publish_time_earlier_than_generation_is_the_next_day()
    {
        Assert.AreEqual(Local(30, 8, 0), Settings("23:00", "08:00").PublishSlotFor(Day));
    }

    /// <summary>发布时间晚于生成时间：当天晚上，也就是用户设 23:16 时想看的那件事。</summary>
    [TestMethod]
    public void A_publish_time_later_than_generation_is_the_same_day()
    {
        Assert.AreEqual(Local(29, 23, 16), Settings("23:00", "23:16").PublishSlotFor(Day));
    }

    /// <summary>两个时刻相等按当天算：生成完就该轮到发布，而不是再等 24 小时。</summary>
    [TestMethod]
    public void An_equal_time_is_still_the_same_day()
    {
        Assert.AreEqual(Local(29, 23, 0), Settings("23:00", "23:00").PublishSlotFor(Day));
    }
}
