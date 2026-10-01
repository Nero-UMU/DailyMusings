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

    /// <summary>Lower bound of the body length in Chinese characters (decisions A.24, A.28).</summary>
    int WritingMinCharacters,

    /// <summary>Upper bound of the body length in Chinese characters.</summary>
    int WritingMaxCharacters,

    /// <summary>
    /// How far outside that range the model may go on purpose; <c>0</c> means the range is strict. The material
    /// decides whether the article lands a little short or a little long.
    /// </summary>
    int WritingTolerance,

    /// <summary><c>first</c>, <c>second</c> or <c>third</c>.</summary>
    string WritingPerson,

    /// <summary>The user's rules, in the order they arranged them. Empty is a valid choice.</summary>
    IReadOnlyList<WritingRuleDto> WritingRules,

    /// <summary>How many days of already-published articles are handed to the model as continuity; 0 = none.</summary>
    int WritingRecentArticleDays);

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
    int? WritingMinCharacters = null,
    int? WritingMaxCharacters = null,
    int? WritingTolerance = null,
    string? WritingPerson = null,

    /// <summary>
    /// Absent means "leave the rules alone"; an empty array means "the user deleted every rule", which is a real
    /// choice and must not be turned back into the defaults.
    /// </summary>
    IReadOnlyList<WritingRuleDto>? WritingRules = null,

    /// <summary>把最近多少天的成稿一并交给模型；<c>0</c> 表示不发，缺省表示不改。</summary>
    int? WritingRecentArticleDays = null);
