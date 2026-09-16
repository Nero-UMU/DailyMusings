namespace DailyMusings.Contracts;

/// <summary>
/// The instance's content and schedule settings (docs/开发指导.md §4.1, §7, §11.1).
/// <para>
/// Times are local wall-clock times in the content time zone, not instants: "generate at 23:00" means 23:00 where
/// the user lives, and storing it as an instant would silently move the schedule when the zone changes.
/// </para>
/// </summary>
public sealed record ContentSettingsDto(
    string TimeZoneId,
    string GenerationLocalTime,
    string PublishLocalTime,
    int PublishWindowMinutes,
    int AudioRetentionDays);

public sealed record UpdateContentSettingsRequest(
    string? TimeZoneId,
    string? GenerationLocalTime,
    string? PublishLocalTime,
    int? PublishWindowMinutes,
    int? AudioRetentionDays);
