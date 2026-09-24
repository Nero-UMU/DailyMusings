namespace DailyMusings.Admin.Shared;

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
}
