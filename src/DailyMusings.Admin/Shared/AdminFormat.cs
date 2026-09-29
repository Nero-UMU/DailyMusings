namespace DailyMusings.Admin.Shared;

using System.Globalization;

/// <summary>
/// 页面上反复出现的两种格式化：字节大小与时间。
/// <para>
/// 集中在这里而不是每个分区各写一份：同一份备份在两个页面上显示成两个大小，是最容易让人怀疑「到底哪份对」的
/// 那种不一致。时间一律按服务器本地时区显示，并在标签里写明是本地时间。
/// </para>
/// </summary>
public static class AdminFormat
{
    /// <summary>Human-readable byte size. Never rounds a non-empty value down to "0 B".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB",
    };

    /// <summary>A UTC instant in the server's local zone; <c>null</c> reads as "—".</summary>
    public static string When(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    /// <summary>A UTC instant as an exact moment (for audit-style fields).</summary>
    public static string Utc(DateTimeOffset? value) =>
        value is { } moment ? $"{moment:yyyy-MM-dd HH:mm:ss} UTC" : "—";

    /// <summary>
    /// 读一个「一天中的时刻」输入，接受浏览器和人都可能给出的几种写法。
    /// <para>
    /// 这个产品打印和存储的都是 <c>HH:mm</c>，但页面上读到的**不一定是它**：对
    /// <c>&lt;input type="time"&gt;</c>，Blazor 的 change 事件带回来的是往返格式（字段显示 23:45，事件里是
    /// 「23:45:00」）；而纯文本框里人会写「3:30」。曾经只用 <c>HH:mm</c> 解析，后果是**只要动过任何一个
    /// 时间字段就再也保存不了**，报错还把两个字段一起冤枉（见 CHANGELOG 2026-09-29 第七轮）。
    /// </para>
    /// <para>
    /// 秒被丢掉：存储、接口和调度器一律按分钟粒度，不能因为一个输入法差异让某个任务悄悄挪到 23:45:30。
    /// </para>
    /// </summary>
    public static bool TryParseTimeOfDay(string? value, out TimeOnly time)
    {
        time = default;

        var text = (value ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return false;
        }

        foreach (var format in TimeFormats)
        {
            if (TimeOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                time = new TimeOnly(parsed.Hour, parsed.Minute);
                return true;
            }
        }

        return false;
    }

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"];
}
