namespace DailyMusings.Contracts;

/// <summary>One editable rule of the writing spec, as it crosses the wire (decision A.24).</summary>
public sealed record WritingRuleDto(string Title, string Instruction);

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
    int AudioRetentionDays,

    /// <summary>
    /// How long captured content is kept after the day is confirmed. <c>-1</c> means "keep everything", which
    /// is the default: a fresh instance never deletes anything on its own.
    /// </summary>
    int ContentRetentionDays,
    string DraftDirectory,
    string PublishedDirectory,
    string HexoFrontMatterTemplate,

    /// <summary>Target body length in Chinese characters (decision A.24).</summary>
    int WritingTargetCharacters,

    /// <summary><c>first</c>, <c>second</c> or <c>third</c>.</summary>
    string WritingPerson,

    /// <summary>The user's rules, in the order they arranged them. Empty is a valid choice.</summary>
    IReadOnlyList<WritingRuleDto> WritingRules);

public sealed record UpdateContentSettingsRequest(
    string? TimeZoneId,
    string? GenerationLocalTime,
    string? PublishLocalTime,
    int? PublishWindowMinutes,
    int? AudioRetentionDays,
    int? ContentRetentionDays = null,
    string? DraftDirectory = null,
    string? PublishedDirectory = null,
    string? HexoFrontMatterTemplate = null,
    int? WritingTargetCharacters = null,
    string? WritingPerson = null,

    /// <summary>
    /// Absent means "leave the rules alone"; an empty array means "the user deleted every rule", which is a real
    /// choice and must not be turned back into the defaults.
    /// </summary>
    IReadOnlyList<WritingRuleDto>? WritingRules = null);
