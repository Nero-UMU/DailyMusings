using System.Globalization;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Configuration;

/// <summary>
/// Instance-level content settings (docs/开发指导.md §7, §11.1, §15.1). Defaults here are the documented
/// defaults, so an instance that has never been configured still behaves exactly as the guide describes.
/// <para>
/// Keys are stable strings because they are persisted in the settings table and exported in backups.
/// </para>
/// </summary>
public sealed record ContentSettings
{
    public const string TimeZoneKey = "content.timeZone";
    public const string GenerationTimeKey = "content.generationLocalTime";
    public const string PublishTimeKey = "publish.scheduledLocalTime";
    public const string PublishWindowKey = "publish.windowMinutes";
    public const string AudioRetentionKey = "retention.audioDays";

    /// <summary>Default content time zone, per §7.</summary>
    public const string DefaultTimeZoneId = ContentTimeZone.DefaultId;

    /// <summary>Default generation slot: 23:00 local.</summary>
    public static readonly TimeOnly DefaultGenerationTime = new(23, 0);

    /// <summary>Default publication slot: 08:00 local the next day.</summary>
    public static readonly TimeOnly DefaultPublishTime = new(8, 0);

    /// <summary>Default execution window, per decision A.2.</summary>
    public const int DefaultPublishWindowMinutes = 120;

    /// <summary>Default audio retention, per decision A.1. Negative means "keep forever".</summary>
    public const int DefaultAudioRetentionDays = 30;

    public static ContentSettings Default { get; } = new(
        DefaultTimeZoneId,
        DefaultGenerationTime,
        DefaultPublishTime,
        DefaultPublishWindowMinutes,
        DefaultAudioRetentionDays);

    public ContentSettings(
        string timeZoneId,
        TimeOnly generationLocalTime,
        TimeOnly publishLocalTime,
        int publishWindowMinutes,
        int audioRetentionDays)
    {
        TimeZoneId = timeZoneId;
        GenerationLocalTime = generationLocalTime;
        PublishLocalTime = publishLocalTime;
        PublishWindowMinutes = publishWindowMinutes;
        AudioRetentionDays = audioRetentionDays;
    }

    public string TimeZoneId { get; init; }

    public TimeOnly GenerationLocalTime { get; init; }

    public TimeOnly PublishLocalTime { get; init; }

    public int PublishWindowMinutes { get; init; }

    /// <summary>Days to keep a recording. <c>-1</c> means "keep forever", <c>0</c> means "delete as soon as
    /// the day's draft is confirmed" (decision A.1).</summary>
    public int AudioRetentionDays { get; init; }

    public TimeSpan PublishWindow => TimeSpan.FromMinutes(PublishWindowMinutes);

    public ContentTimeZone ResolveTimeZone() => ContentTimeZone.FromId(TimeZoneId);

    public ContentCalendar CreateCalendar() => new(ResolveTimeZone());

    /// <summary>Reads a settings snapshot out of persisted key/value pairs, falling back to the defaults.</summary>
    public static ContentSettings FromValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new ContentSettings(
            Read(values, TimeZoneKey, DefaultTimeZoneId),
            ReadTime(values, GenerationTimeKey, DefaultGenerationTime),
            ReadTime(values, PublishTimeKey, DefaultPublishTime),
            ReadInt(values, PublishWindowKey, DefaultPublishWindowMinutes),
            ReadInt(values, AudioRetentionKey, DefaultAudioRetentionDays));
    }

    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TimeZoneKey] = TimeZoneId,
        [GenerationTimeKey] = FormatTime(GenerationLocalTime),
        [PublishTimeKey] = FormatTime(PublishLocalTime),
        [PublishWindowKey] = PublishWindowMinutes.ToString(CultureInfo.InvariantCulture),
        [AudioRetentionKey] = AudioRetentionDays.ToString(CultureInfo.InvariantCulture),
    };

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Read(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static TimeOnly ReadTime(IReadOnlyDictionary<string, string> values, string key, TimeOnly fallback) =>
        values.TryGetValue(key, out var value) &&
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) &&
        int.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>Supplies the current settings snapshot; a fresh read per call keeps it consistent with the store.</summary>
public interface IContentSettingsProvider
{
    Task<ContentSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Supplies a calendar bound to the configured content time zone. Exposed separately from the settings record so
/// that ingestion does not have to re-parse the time zone on every upload, and so that reading it is one
/// well-named call at the point where the §7 rules apply.
/// </summary>
public interface IContentCalendarProvider
{
    Task<ContentCalendar> GetCalendarAsync(CancellationToken cancellationToken);
}
