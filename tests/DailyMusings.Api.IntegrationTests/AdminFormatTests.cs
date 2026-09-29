using DailyMusings.Admin.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// 管理页读时间字段的那条路（CHANGELOG 2026-09-29 第七轮）。
/// <para>
/// 回归的是这个缺陷：对 <c>&lt;input type="time"&gt;</c>，Blazor 的 change 事件交回来的是**带秒的往返格式**
/// （字段显示 23:45，事件里是「23:45:00」），而页面当时只按 <c>HH:mm</c> 解析——于是**只要动过任何一个时间
/// 字段，发布设置就再也保存不了**，报错还把生成与发布两个字段一起冤枉。这些用例把「浏览器会给出的形状」和
/// 「人不按补零写法敲的形状」都钉住。
/// </para>
/// </summary>
[TestClass]
public class AdminFormatTests
{
    /// <summary>The value the browser actually produced for a field showing 23:45 — the whole reason for the fix.</summary>
    [TestMethod]
    public void The_round_trip_form_a_time_input_hands_back_is_accepted()
    {
        Assert.IsTrue(AdminFormat.TryParseTimeOfDay("23:45:00", out var time), "23:45:00 must be readable.");
        Assert.AreEqual(new TimeOnly(23, 45), time);
    }

    [TestMethod]
    public void The_forms_this_product_prints_and_people_type_are_accepted()
    {
        foreach (var (text, expected) in new[]
        {
            ("23:45", new TimeOnly(23, 45)),
            ("08:00", new TimeOnly(8, 0)),
            ("00:00", new TimeOnly(0, 0)),
            ("3:30", new TimeOnly(3, 30)),
            ("03:30:59", new TimeOnly(3, 30)),
            (" 09:15 ", new TimeOnly(9, 15)),
        })
        {
            Assert.IsTrue(AdminFormat.TryParseTimeOfDay(text, out var parsed), $"«{text}» must be readable.");
            Assert.AreEqual(expected, parsed, $"«{text}»");
        }
    }

    /// <summary>
    /// 秒一律丢掉：存储、接口与调度器都按分钟粒度，不能因为输入法差异让某个任务挪到 23:45:30。
    /// </summary>
    [TestMethod]
    public void Seconds_are_dropped_rather_than_carried_into_a_schedule()
    {
        Assert.IsTrue(AdminFormat.TryParseTimeOfDay("23:45:30", out var time));
        Assert.AreEqual(0, time.Second);
        Assert.AreEqual(new TimeOnly(23, 45), time);
    }

    /// <summary>
    /// 读不出来就如实说读不出来：空值与垃圾值都必须被拒（页面据此分别报「还是空的」与「看不懂」）。
    /// </summary>
    [TestMethod]
    public void Empty_and_garbage_are_rejected()
    {
        foreach (var text in new string?[] { null, string.Empty, "   ", "abc", "23", "23:45:30:00", "25:00", "23:60", "-1:00", "23：45" })
        {
            Assert.IsFalse(AdminFormat.TryParseTimeOfDay(text, out var parsed), $"«{text}» must be rejected.");
            Assert.AreEqual(default, parsed);
        }
    }
}
