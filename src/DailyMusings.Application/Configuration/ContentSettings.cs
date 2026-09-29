using System.Globalization;
using System.Text.Json;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
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

    /// <summary>
    /// How long the captured content itself is kept after a day is confirmed. <c>-1</c> means "keep everything",
    /// which is the default: a fresh instance never deletes anything on its own, and the audio policy above is
    /// the only retention a user gets without asking for more.
    /// </summary>
    public const string ContentRetentionKey = "retention.contentDays";
    public const string DraftDirectoryKey = "publish.draftDirectory";
    public const string PublishedDirectoryKey = "publish.publishedDirectory";
    public const string HexoTemplateKey = "publish.hexoFrontMatterTemplate";

    // The writing spec (decision A.24, revised by A.28). Separate keys rather than one blob so that the settings
    // table and a backup stay readable, and so that "the rules are empty" is distinguishable from "nobody ever set
    // rules" — the first is a choice the user made, the second is the only case that gets the defaults.
    public const string WritingMinCharactersKey = "writing.minCharacters";
    public const string WritingMaxCharactersKey = "writing.maxCharacters";
    public const string WritingToleranceKey = "writing.tolerance";
    public const string WritingPersonKey = "writing.person";
    public const string WritingRulesKey = "writing.rules";

    /// <summary>
    /// 旧键（单一「目标字数」）。只读，不再写：A.28 把「一个目标值」换成了区间加公差，但已经存过 300 的实例
    /// 不该在看到新界面时被打回默认值——读到它就把它当作**最多字数**（旧的 300 立刻变成「最少 50、最多 300」）。
    /// </summary>
    public const string WritingTargetCharactersKey = "writing.targetCharacters";

    public const string DefaultDraftDirectory = "drafts";
    public const string DefaultPublishedDirectory = "posts";

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

    /// <summary>Content retention is off by default: nothing is deleted unless the user asks for it.</summary>
    public const int DefaultContentRetentionDays = ContentRetentionPolicy.KeepForever;

    public static ContentSettings Default { get; } = new(
        DefaultTimeZoneId,
        DefaultGenerationTime,
        DefaultPublishTime,
        DefaultPublishWindowMinutes,
        DefaultAudioRetentionDays,
        DefaultContentRetentionDays,
        DefaultDraftDirectory,
        DefaultPublishedDirectory,
        Domain.Publishing.MarkdownTemplate.Default);

    public ContentSettings(
        string timeZoneId,
        TimeOnly generationLocalTime,
        TimeOnly publishLocalTime,
        int publishWindowMinutes,
        int audioRetentionDays,
        int contentRetentionDays = DefaultContentRetentionDays,
        string draftDirectory = DefaultDraftDirectory,
        string publishedDirectory = DefaultPublishedDirectory,
        string? hexoFrontMatterTemplate = null,
        WritingSettings? writing = null)
    {
        TimeZoneId = timeZoneId;
        GenerationLocalTime = generationLocalTime;
        PublishLocalTime = publishLocalTime;
        PublishWindowMinutes = publishWindowMinutes;
        AudioRetentionDays = audioRetentionDays;
        ContentRetentionDays = contentRetentionDays;
        DraftDirectory = draftDirectory;
        PublishedDirectory = publishedDirectory;
        HexoFrontMatterTemplate = hexoFrontMatterTemplate ?? Domain.Publishing.MarkdownTemplate.Default;
        Writing = writing ?? WritingSettings.Default;
    }

    public string TimeZoneId { get; init; }

    public TimeOnly GenerationLocalTime { get; init; }

    public TimeOnly PublishLocalTime { get; init; }

    public int PublishWindowMinutes { get; init; }

    /// <summary>Days to keep a recording. <c>-1</c> means "keep forever", <c>0</c> means "delete as soon as
    /// the day's draft is confirmed" (decision A.1).</summary>
    public int AudioRetentionDays { get; init; }

    /// <summary>Days to keep the captured content itself after confirmation. <c>-1</c> (the default) keeps it
    /// forever, and <c>0</c> deletes it as soon as the day is confirmed.</summary>
    public int ContentRetentionDays { get; init; }

    public string DraftDirectory { get; init; }

    public string PublishedDirectory { get; init; }

    public string HexoFrontMatterTemplate { get; init; }

    /// <summary>The writing spec handed to the model on every generation (decision A.24).</summary>
    public WritingSettings Writing { get; init; }

    public AudioRetentionPolicy ResolveAudioRetention() => new(AudioRetentionDays);

    public ContentRetentionPolicy ResolveContentRetention() => new(ContentRetentionDays);

    public TimeSpan PublishWindow => TimeSpan.FromMinutes(PublishWindowMinutes);

    public ContentTimeZone ResolveTimeZone() => ContentTimeZone.FromId(TimeZoneId);

    public ContentCalendar CreateCalendar() => new(ResolveTimeZone());

    /// <summary>
    /// 某一天的稿件应当在什么时候自动发布（§11.1）。
    /// <para>
    /// 规则：发布时刻按**它出现在生成时刻之后的那一次**来解释——发布时间晚于生成时间（例如 23:00 生成、
    /// 23:16 发布）就是当天；否则（默认 23:00 生成、次日 08:00 发布）算次日。
    /// </para>
    /// <para>
    /// 旧实现无条件按「内容日期 +1 天」计算，于是把「当天晚上发布」这种再自然不过的配置推到了 24 小时之后：
    /// 用户勾上自动发布、把发布时间设成生成之后十几分钟，到点却什么都不会发生，而页面又从不显示它算出的时刻
    /// （见 CHANGELOG 2026-09-29 第八轮）。时刻只在这里算一次，调度、提醒与手动入队三条路都用它，否则三条路
    /// 会各自漂移。
    /// </para>
    /// </summary>
    public DateTimeOffset PublishSlotFor(ContentDate contentDate)
    {
        var calendar = CreateCalendar();

        return PublishLocalTime >= GenerationLocalTime
            ? calendar.AtLocalTime(contentDate, PublishLocalTime)
            : calendar.AtLocalTime(contentDate.AddDays(1), PublishLocalTime);
    }

    /// <summary>Reads a settings snapshot out of persisted key/value pairs, falling back to the defaults.</summary>
    public static ContentSettings FromValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new ContentSettings(
            Read(values, TimeZoneKey, DefaultTimeZoneId),
            ReadTime(values, GenerationTimeKey, DefaultGenerationTime),
            ReadTime(values, PublishTimeKey, DefaultPublishTime),
            ReadInt(values, PublishWindowKey, DefaultPublishWindowMinutes),
            ReadInt(values, AudioRetentionKey, DefaultAudioRetentionDays),
            ReadInt(values, ContentRetentionKey, DefaultContentRetentionDays),
            Read(values, DraftDirectoryKey, DefaultDraftDirectory),
            Read(values, PublishedDirectoryKey, DefaultPublishedDirectory),
            Read(values, HexoTemplateKey, Domain.Publishing.MarkdownTemplate.Default),
            new WritingSettings(
                ReadInt(values, WritingMinCharactersKey, WritingSettings.DefaultMinCharacters),
                ReadMaxCharacters(values),
                ReadInt(values, WritingToleranceKey, WritingSettings.DefaultCharacterTolerance),
                ReadPerson(values),
                ReadRules(values)));
    }

    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TimeZoneKey] = TimeZoneId,
        [GenerationTimeKey] = FormatTime(GenerationLocalTime),
        [PublishTimeKey] = FormatTime(PublishLocalTime),
        [PublishWindowKey] = PublishWindowMinutes.ToString(CultureInfo.InvariantCulture),
        [AudioRetentionKey] = AudioRetentionDays.ToString(CultureInfo.InvariantCulture),
        [ContentRetentionKey] = ContentRetentionDays.ToString(CultureInfo.InvariantCulture),
        [DraftDirectoryKey] = DraftDirectory,
        [PublishedDirectoryKey] = PublishedDirectory,
        [HexoTemplateKey] = HexoFrontMatterTemplate,
        [WritingMinCharactersKey] = Writing.MinCharacters.ToString(CultureInfo.InvariantCulture),
        [WritingMaxCharactersKey] = Writing.MaxCharacters.ToString(CultureInfo.InvariantCulture),
        [WritingToleranceKey] = Writing.CharacterTolerance.ToString(CultureInfo.InvariantCulture),
        [WritingPersonKey] = FormatPerson(Writing.Person),
        [WritingRulesKey] = JsonSerializer.Serialize(Writing.Rules, RuleJson),
    };

    /// <summary>
    /// 最多字数：新键优先，其次读旧的单一目标字数（A.28 的兼容路径），都没有才是默认值。
    /// </summary>
    private static int ReadMaxCharacters(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue(WritingMaxCharactersKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return ReadInt(values, WritingTargetCharactersKey, WritingSettings.DefaultMaxCharacters);
    }

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Stable wire names for the person, so the stored value stays readable and reorder-proof.</summary>
    public const string WritingPersonFirst = "first";
    public const string WritingPersonSecond = "second";
    public const string WritingPersonThird = "third";

    public static string FormatPerson(WritingPerson person) => person switch
    {
        WritingPerson.First => WritingPersonFirst,
        WritingPerson.Second => WritingPersonSecond,
        WritingPerson.Third => WritingPersonThird,
        _ => throw new ArgumentOutOfRangeException(nameof(person), person, "Unknown writing person."),
    };

    public static bool TryParsePerson(string? value, out WritingPerson person)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case WritingPersonFirst:
                person = WritingPerson.First;
                return true;
            case WritingPersonSecond:
                person = WritingPerson.Second;
                return true;
            case WritingPersonThird:
                person = WritingPerson.Third;
                return true;
            default:
                person = WritingPerson.First;
                return false;
        }
    }

    private static WritingPerson ReadPerson(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue(WritingPersonKey, out var raw) && TryParsePerson(raw, out var person)
            ? person
            : WritingSettings.Default.Person;

    /// <summary>
    /// The rules as stored. An absent key means the user has never touched them, so they get the defaults; a
    /// present but empty value means they deliberately cleared the list, and that choice is honoured. Unreadable
    /// JSON is treated like an absent key: the same reasoning as a malformed job payload (§14) — a spec nobody
    /// can parse is not a spec, and falling back to the documented default is better than failing a generation.
    /// </summary>
    private static IReadOnlyList<WritingRule> ReadRules(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(WritingRulesKey, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return values.ContainsKey(WritingRulesKey) ? [] : WritingSettings.DefaultRules;
        }

        try
        {
            return JsonSerializer.Deserialize<List<WritingRule>>(json, RuleJson) ?? [];
        }
        catch (JsonException)
        {
            return WritingSettings.DefaultRules;
        }
    }

    private static readonly JsonSerializerOptions RuleJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
