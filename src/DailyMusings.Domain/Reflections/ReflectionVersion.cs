using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Domain.Reflections;

/// <summary>Who the article is written as (docs/开发指导.md §8.4, decision A.24).</summary>
public enum WritingPerson
{
    First = 0,
    Second = 1,
    Third = 2,
}

/// <summary>
/// One rule in the writing spec: a short label and the instruction handed to the model.
/// <para>
/// The spec is a list of these rather than a set of fixed fields because the user owns it. The seeded defaults
/// are a starting point they may rewrite, reorder or delete entirely (decision A.24).
/// </para>
/// </summary>
public sealed record WritingRule(string Title, string Instruction)
{
    public const int MaxTitleLength = 40;
    public const int MaxInstructionLength = 500;
}

/// <summary>
/// The writing spec handed to the model on every generation (docs/开发指导.md §8.4, decision A.24).
/// <para>
/// This is a user-owned preference, not a per-day decision: a generation job reads the current spec when it runs,
/// and the spec that was in force is recorded on the version it produced. The length is a range of Chinese
/// characters rather than a small/medium/large enum, because "somewhere between 50 and 300 characters" is what a
/// person actually wants and an enum only ever approximates it; the optional tolerance lets the material decide
/// where in (or just outside) that range the article lands (decision A.28).
/// </para>
/// </summary>
public sealed record WritingSettings
{
    /// <summary>字数区间的默认值（附录 A.28）：最少 50、最多 300。</summary>
    public const int DefaultMinCharacters = 50;
    public const int DefaultMaxCharacters = 300;

    /// <summary>默认公差 20 个字：允许模型按素材多少在区间外浮动这么多（<c>0</c> = 严格落在区间内）。</summary>
    public const int DefaultCharacterTolerance = 20;

    /// <summary>
    /// 默认把最近 7 天的成稿一并交给模型。用户要的是「延续感」——今天这篇接得上前几天写的，
    /// 而不是从原始随想里把旧事重新组织一遍（附录 A.41 续记）。<c>0</c> 表示不交。
    /// </summary>
    public const int DefaultRecentArticleDays = 7;

    public const int MinAllowedCharacters = 1;
    public const int MaxAllowedCharacters = 5000;
    public const int MaxAllowedTolerance = 500;
    public const int MaxRules = 20;
    public const int MaxRecentArticleDays = 60;

    public WritingSettings(
        int minCharacters,
        int maxCharacters,
        int characterTolerance,
        WritingPerson person,
        IReadOnlyList<WritingRule>? rules = null,
        int recentArticleDays = DefaultRecentArticleDays)
    {
        MinCharacters = minCharacters;
        MaxCharacters = maxCharacters;
        CharacterTolerance = characterTolerance;
        Person = person;
        RecentArticleDays = recentArticleDays;

        // Never null: a spec with no rules is a legitimate choice (the user deleted every default), but a null
        // list would turn that choice into a crash the first time something enumerated it.
        Rules = rules ?? [];
    }

    /// <summary>
    /// 把最近多少天的成稿（已发布的博客正文）一并发给模型；<c>0</c> 表示不发。
    /// <para>
    /// 与 §8.3 的历史检索是两回事：检索给的是往日的**原始随想**，这里给的是往日的**成稿**。前者用来
    /// 呼应具体的事，后者用来接上行文与延续。两者都只能提一嘴（附录 A.41）。
    /// </para>
    /// </summary>
    public int RecentArticleDays { get; init; }

    /// <summary>Lower bound of the body length in Chinese characters, Markdown markers excluded.</summary>
    public int MinCharacters { get; init; }

    /// <summary>Upper bound of the body length in Chinese characters, Markdown markers excluded.</summary>
    public int MaxCharacters { get; init; }

    /// <summary>
    /// How far outside the range the model may go on purpose; <c>0</c> means the range is strict.
    /// <para>
    /// It exists because the material decides how much there is to say: a day with three sentences should not be
    /// padded out to fit, and a day with a lot in it should not be cut off mid-thought. The range is the intention,
    /// the tolerance is the slack (decision A.28).
    /// </para>
    /// </summary>
    public int CharacterTolerance { get; init; }

    public bool AllowsTolerance => CharacterTolerance > 0;

    /// <summary>区间加公差之后的真实下限，也就是交给模型的数字。</summary>
    public int ToleratedMinCharacters => Math.Max(MinAllowedCharacters, MinCharacters - CharacterTolerance);

    /// <summary>区间加公差之后的真实上限，也就是交给模型的数字。</summary>
    public int ToleratedMaxCharacters => MaxCharacters + CharacterTolerance;

    public WritingPerson Person { get; init; }

    /// <summary>
    /// The rules in the order the user arranged them. Empty means "no extra rules", never "seed the defaults
    /// again" — only an instance that has never stored a spec gets the defaults.
    /// </summary>
    public IReadOnlyList<WritingRule> Rules { get; init; }

    /// <summary>
    /// The starting spec. Every entry is editable and deletable from the publishing settings page; the wording
    /// aims at the failure modes of machine-written Chinese (inflated endings, connective filler, worn-out
    /// buzzwords) rather than at a literary style.
    /// </summary>
    public static IReadOnlyList<WritingRule> DefaultRules { get; } =
    [
        new("行文风格",
            "平实、克制，像写给自己看的记录。多用具体的名词和动作，少用抽象评价和形容词；不刻意升华，" +
            "不强行给出意义，也不为了好看而堆砌辞藻。句子长短交替，允许出现短句和停顿。"),
        new("结构与段落",
            "自然分段，每段 2 到 5 句，全文 3 到 6 段。不写小标题，不用列表和编号，不用 emoji。" +
            "段落之间靠内容推进，不靠「首先」「其次」「最后」这类连接词。"),
        new("标题",
            "标题是一句具体的话或一个具体的意象，6 到 16 个字，不加书名号、不加感叹号、" +
            "不写成「随想一则」这类空泛的说法，也不概括全文。"),
        new("开头与结尾",
            "开头直接进入当天的内容，不写「今天」「又是平凡的一天」这类开场套话。" +
            "结尾停在具体的事或感受上，不做总结，不写金句，不替读者拔高。"),
        new("用词禁区",
            "不用「总之」「综上所述」「不难看出」「值得一提的是」「在这个快节奏的时代」这类套话；" +
            "不用「治愈」「赋能」「内耗」「破防」这类被用滥的流行词；不出现 AI 自我介绍，也不称呼读者。"),
    ];

    public static WritingSettings Default { get; } = new(
        DefaultMinCharacters,
        DefaultMaxCharacters,
        DefaultCharacterTolerance,
        WritingPerson.First,
        DefaultRules);

    /// <summary>
    /// Rejects a spec that cannot be handed to a model. Called where a user-supplied spec enters the system, so a
    /// typo is reported at the moment it is saved rather than silently producing a bad article at 23:00.
    /// </summary>
    public void Validate()
    {
        if (MinCharacters is < MinAllowedCharacters or > MaxAllowedCharacters)
        {
            throw new DomainException(
                "writing.min.out_of_range",
                $"最少字数必须在 {MinAllowedCharacters} 到 {MaxAllowedCharacters} 之间。");
        }

        if (MaxCharacters is < MinAllowedCharacters or > MaxAllowedCharacters)
        {
            throw new DomainException(
                "writing.max.out_of_range",
                $"最多字数必须在 {MinAllowedCharacters} 到 {MaxAllowedCharacters} 之间。");
        }

        if (MinCharacters >= MaxCharacters)
        {
            throw new DomainException(
                "writing.range.invalid",
                $"最少字数必须小于最多字数（现在是 {MinCharacters} 与 {MaxCharacters}）。");
        }

        if (CharacterTolerance is < 0 or > MaxAllowedTolerance)
        {
            throw new DomainException(
                "writing.tolerance.out_of_range",
                $"公差必须在 0 到 {MaxAllowedTolerance} 之间（0 表示不允许浮动）。");
        }

        if (RecentArticleDays is < 0 or > MaxRecentArticleDays)
        {
            throw new DomainException(
                "writing.recent_articles.out_of_range",
                $"成稿天数必须在 0 到 {MaxRecentArticleDays} 之间（0 表示不把往日的成稿发给模型）。");
        }

        if (!Enum.IsDefined(Person))
        {
            throw new DomainException("writing.person.unknown", "人称只能是第一、第二或第三人称。");
        }

        if (Rules.Count > MaxRules)
        {
            throw new DomainException("writing.rules.too_many", $"规范条目最多 {MaxRules} 条。");
        }

        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Title) || rule.Title.Trim().Length > WritingRule.MaxTitleLength)
            {
                throw new DomainException(
                    "writing.rule.title_invalid",
                    $"每条规范都要有标题，且不超过 {WritingRule.MaxTitleLength} 个字。");
            }

            if (string.IsNullOrWhiteSpace(rule.Instruction) ||
                rule.Instruction.Trim().Length > WritingRule.MaxInstructionLength)
            {
                throw new DomainException(
                    "writing.rule.instruction_invalid",
                    $"每条规范都要有内容，且不超过 {WritingRule.MaxInstructionLength} 个字。");
            }
        }
    }
}

/// <summary>Which model produced a version. Configuration and secrets stay on the server (§8.1).</summary>
public sealed record ModelInfo(string ModelName);

/// <summary>
/// An immutable-in-spirit snapshot of one generated (or re-generated) draft
/// (docs/开发指导.md §6.4).
/// <para>
/// Version rows are never deleted — only the three slot pointers on <see cref="Reflection"/> rotate —
/// so <see cref="Reflection.ConfirmedVersionId"/> can never dangle.
/// </para>
/// </summary>
public sealed class ReflectionVersion
{
    private readonly List<string> _tags = [];
    private readonly List<string> _categories = [];
    private readonly List<SourceReference> _sources = [];
    private readonly List<UnsourcedClaim> _unsourcedClaims = [];
    private readonly List<TopicId> _topicIds = [];

    private ReflectionVersion(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ReflectionId = reflectionId;
        Title = title;
        Summary = summary;
        Body = body;
        Settings = settings;
        CreatedAtUtc = createdAtUtc;
    }

    public ReflectionVersionId Id { get; }

    public ReflectionId ReflectionId { get; }

    public string Title { get; private set; }

    public string Summary { get; private set; }

    public string Body { get; private set; }

    public IReadOnlyList<string> Tags => _tags;

    public IReadOnlyList<string> Categories => _categories;

    /// <summary>
    /// The topics this article is about, primary first (§6.2 as revised: the model picks or coins them during
    /// generation, and the admin page may re-file an article).
    /// <para>
    /// Held on the version rather than derived from the day's inputs on purpose. A version is a snapshot of one
    /// article; the inputs' own filing answers a different question ("where is this recording filed") and the
    /// user is free to change it afterwards without silently rewriting what the article was about.
    /// </para>
    /// </summary>
    public IReadOnlyList<TopicId> TopicIds => _topicIds;

    /// <summary>Paragraph/sentence to input mappings for this exact revision of the body.</summary>
    public IReadOnlyList<SourceReference> Sources => _sources;

    /// <summary>
    /// Sentences the second-stage check could not trace to any input (§8.4). Surfaced as highlights
    /// with a reason; never a hard block — the user may still publish after confirming.
    /// </summary>
    public IReadOnlyList<UnsourcedClaim> UnsourcedClaims => _unsourcedClaims;

    /// <summary>
    /// When the second-stage check last ran for this text, or <c>null</c> when it never has.
    /// <para>
    /// Without this, an empty <see cref="UnsourcedClaims"/> is ambiguous: "checked and found nothing" and
    /// "never checked" look identical, and the client would present the reassuring one for both. §8.4 makes the
    /// check a requirement, so the product must be able to say whether it happened.
    /// </para>
    /// </summary>
    public DateTimeOffset? SourcesCheckedAtUtc { get; private set; }

    public WritingSettings Settings { get; private set; }

    public ModelInfo? ModelInfo { get; private set; }

    public string? PromptVersion { get; private set; }

    /// <summary>
    /// Set as soon as a human edits the text. Rotation refuses to move such a version out of the
    /// working slot unless the user explicitly confirmed the loss (§6.4).
    /// </summary>
    public bool HasManualEdits { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? EditedAtUtc { get; private set; }

    public IReadOnlyList<string> Paragraphs => ParagraphSplitter.Split(Body);

    public static ReflectionVersion CreateGenerated(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        ModelInfo? modelInfo,
        string? promptVersion,
        DateTimeOffset createdAtUtc,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? categories = null,
        IEnumerable<TopicId>? topics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var version = new ReflectionVersion(
            id,
            reflectionId,
            title ?? string.Empty,
            summary ?? string.Empty,
            body ?? string.Empty,
            settings,
            createdAtUtc)
        {
            ModelInfo = modelInfo,
            PromptVersion = promptVersion,
        };

        version.ReplaceTags(tags, categories);

        // The list's order is the decision: the first entry is the day's main topic, the rest are secondary.
        var attached = CleanTopics(topics);
        version.AttachTopics(attached.Count > 0 ? attached[0] : null, attached.Skip(1).ToArray());

        return version;
    }

    /// <summary>Rehydrates from storage.</summary>
    public static ReflectionVersion Rehydrate(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        ModelInfo? modelInfo,
        string? promptVersion,
        bool hasManualEdits,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? editedAtUtc,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? categories = null,
        DateTimeOffset? sourcesCheckedAtUtc = null,
        IEnumerable<TopicId>? topics = null)
    {
        var version = new ReflectionVersion(id, reflectionId, title, summary, body, settings, createdAtUtc)
        {
            ModelInfo = modelInfo,
            PromptVersion = promptVersion,
            HasManualEdits = hasManualEdits,
            EditedAtUtc = editedAtUtc,
            SourcesCheckedAtUtc = sourcesCheckedAtUtc,
        };

        version.ReplaceTags(tags, categories);
        version.AttachTopics(topics?.FirstOrDefault(), topics?.Skip(1) ?? []);

        return version;
    }

    /// <summary>
    /// Records a human edit. This is what makes the version protected from silent regeneration, so it
    /// always flips <see cref="HasManualEdits"/> — there is no "edit without marking" overload.
    /// </summary>
    public void Edit(string title, string summary, string body, DateTimeOffset at)
    {
        Title = title ?? string.Empty;
        Summary = summary ?? string.Empty;
        Body = body ?? string.Empty;
        HasManualEdits = true;
        EditedAtUtc = at;

        // Any source mapping or drift analysis produced for the previous text is now stale by
        // definition; clearing it is what makes SourceLocator honest about drift instead of pointing at
        // the wrong sentence.
        _sources.Clear();
        _unsourcedClaims.Clear();

        // The check result described the old text, so the version goes back to "not checked" rather than
        // keeping a clean bill of health for a sentence nobody examined.
        SourcesCheckedAtUtc = null;
    }

    public void ReplaceTags(IEnumerable<string>? tags, IEnumerable<string>? categories)
    {
        _tags.Clear();
        _tags.AddRange(Clean(tags));

        _categories.Clear();
        _categories.AddRange(Clean(categories));

        static IEnumerable<string> Clean(IEnumerable<string>? values) =>
            (values ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Replaces the topics this article is about (docs/开发指导.md §6.2 as revised).
    /// <para>
    /// <paramref name="primary"/> is the single "mainly about" topic and is stored first; it may be
    /// <c>null</c> when the model named only secondary themes. Duplicates are dropped and the primary can
    /// never also appear among the secondary ones, for the same reason an input's filing enforces that
    /// (§6.2): a list that says "this is mainly A, and also A" is not a list anyone can act on.
    /// </para>
    /// </summary>
    public void AttachTopics(TopicId? primary, IEnumerable<TopicId> secondary)
    {
        var cleaned = CleanTopics(secondary);
        cleaned.RemoveAll(topicId => topicId == primary);

        _topicIds.Clear();

        if (primary is { IsEmpty: false } main)
        {
            _topicIds.Add(main);
        }

        _topicIds.AddRange(cleaned);
    }

    /// <summary>Attaches the source map produced alongside this version's text.</summary>
    public void AttachSources(IEnumerable<SourceReference> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var incoming = sources.ToList();
        foreach (var source in incoming)
        {
            if (source.ReflectionVersionId != Id)
            {
                throw new DomainException(
                    "reflection.version.source_from_other_version",
                    "A source reference may only be attached to the version it was produced for.");
            }
        }

        _sources.Clear();
        _sources.AddRange(incoming);
    }

    /// <summary>
    /// Records the second-stage findings for this version's text and stamps the check as done.
    /// <para>
    /// The two happen together on purpose: the timestamp is what makes an empty finding list mean "checked and
    /// clean", so a caller that could attach findings without stamping would be able to produce a version whose
    /// check state is unreadable.
    /// </para>
    /// </summary>
    public void AttachUnsourcedClaims(IEnumerable<UnsourcedClaim> claims, DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(claims);

        _unsourcedClaims.Clear();
        _unsourcedClaims.AddRange(claims);
        SourcesCheckedAtUtc = checkedAtUtc;
    }

    /// <summary>
    /// Compares each source reference against the current body and reports which ones still resolve.
    /// Called after an edit to decide between precise highlighting and the §6.5 whole-paragraph
    /// fallback.
    /// </summary>
    public IReadOnlyList<SourceDriftResult> CheckSourceDrift() =>
        _sources.Select(source => new SourceDriftResult(source, SourceLocator.Check(Body, source))).ToList();

    /// <summary>
    /// Drops empty ids and duplicates. Insertion order is preserved, because the first topic is the primary
    /// one and losing that order would silently change what the article claims to be about.
    /// </summary>
    private static List<TopicId> CleanTopics(IEnumerable<TopicId>? values)
    {
        var cleaned = new List<TopicId>();

        foreach (var topicId in values ?? [])
        {
            if (topicId.IsEmpty || cleaned.Contains(topicId))
            {
                continue;
            }

            cleaned.Add(topicId);
        }

        return cleaned;
    }
}
