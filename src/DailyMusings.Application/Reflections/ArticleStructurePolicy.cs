using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections.Sources;

namespace DailyMusings.Application.Reflections;

/// <summary>
/// 判断一版稿子是不是「一段对一条素材」的流水账（docs/开发指导.md 附录 A.41 续记三）。
/// <para>
/// 为什么要有它：提示词里那几条「不许一段对一条素材」是**软约束**，靠模型自觉；用户要的是「在代码层面
/// 强制」。这里给出一个**可计算**的判据，判出不合格就让模型带着明确的改写要求重来一次。
/// </para>
/// <para>
/// 判据刻意保守——**宁可漏判，不可误判**：误判的代价是一版本来合格的文章被无谓地重写，还多花一次模型调用。
/// 只有形状非常明确时才判流水账：素材至少三条、**有引用的段落数不少于素材条数**、且**每一段都只引用了当天的
/// 一条素材**。三条素材以下不判（两三件事写成两段本来就正常）；把两件事写进同一段、或把一件事拆成两段，
/// 都会让判据不成立——那正是我们要的形状。
/// </para>
/// </summary>
public static class ArticleStructurePolicy
{
    /// <summary>低于三条素材时不判：段落与素材的对应关系在那个规模上没有参考价值。</summary>
    public const int MinimumMaterialCount = 3;

    /// <summary>
    /// 判出形状不合规之后追加给模型的话。刻意写得具体——指出上一版哪里不合格、这一版必须怎么改，而不是把
    /// 系统规则再念一遍。
    /// </summary>
    public const string RewriteInstruction = """
        上一版不合格：你是按素材一条一条写下来的，大致一段对一条，读起来是流水账。
        请重写成**一篇文章**：
        - 先定一条贯穿全文的线索——一个感受、一个念头，或这一天里最让你停了一下的那一点，所有素材都服务于它；
        - 重新组织结构，不要按素材出现的时间顺序逐条铺开；几件事可以合在一段里写，一件事也可以分两段；
        - 段落数与素材条数无关；段落之间不要用「然后」「接着」把素材串成流水；
        - 标题必须原样取自正文里的一句话（6 到 16 个字），不许把当天两件事用逗号并成行程表、不许概括全天；
        - 仍然只能使用给定的素材，不得添加素材里没有的事实。
        """;

    /// <summary>
    /// 形状判定。<paramref name="sameDayInputs"/> 是当天素材的那几条输入 id——用它区分「引用的是当天的素材」
    /// 与「引用的是检索出来的历史素材」。
    /// </summary>
    public static ArticleStructureAssessment Assess(
        string title,
        string body,
        IReadOnlyList<GeneratedCitation> citations,
        IReadOnlyCollection<InputEntryId> sameDayInputs)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(sameDayInputs);

        var (titleFromBody, titleLooksConstructed) = AssessTitle(title, body);

        var paragraphs = ParagraphSplitter.Split(body).Count;

        if (sameDayInputs.Count < MinimumMaterialCount || paragraphs == 0)
        {
            return new ArticleStructureAssessment(false, paragraphs, 0, 0, titleFromBody, titleLooksConstructed);
        }

        var day = sameDayInputs.ToHashSet();
        var perParagraph = new Dictionary<int, HashSet<InputEntryId>>();

        foreach (var citation in citations)
        {
            // 段落号必须和来源映射用的是同一套——SourceQuoteLocator 就是 §6.5 存来源时用的那个。
            // 自己按空行切一遍也能算，但两套段号一旦漂移，日志里的「第几段」就和界面上看到的对不上了。
            if (SourceQuoteLocator.Locate(body, citation.Quote) is not { } located)
            {
                continue;
            }

            if (!perParagraph.TryGetValue(located.BlockIndex, out var sources))
            {
                sources = [];
                perParagraph[located.BlockIndex] = sources;
            }

            foreach (var source in citation.InputIds)
            {
                if (day.Contains(source))
                {
                    sources.Add(source);
                }
            }
        }

        var withSources = perParagraph.Count;
        var singleSource = perParagraph.Values.Count(sources => sources.Count == 1);

        return new ArticleStructureAssessment(
            withSources >= sameDayInputs.Count && singleSource == withSources,
            paragraphs,
            withSources,
            singleSource,
            titleFromBody,
            titleLooksConstructed);
    }

    /// <summary>
    /// 标题的两个信号。判据同样是「宁可漏判不可误判」：
    /// <list type="bullet">
    /// <item><c>TitleFromBody</c>：标题去空白与句末标点后，能在正文里原样找到——这正是「标题必须原样取自正文
    /// 里的一句话」那条规则的可计算形式。</item>
    /// <item><c>TitleLooksConstructed</c>：既**不在正文里**，又**把两件事用逗号/顿号/和/与并起来**——用户报的
    /// 「凌晨改代码，中午去婚礼」那一类。两条同时成立才判，所以「改了字的单个意象」不会触发（那是措辞问题，
    /// 不值得为它重写一整篇）。</item>
    /// </list>
    /// </summary>
    private static (bool FromBody, bool LooksConstructed) AssessTitle(string title, string body)
    {
        var trimmed = title.Trim();

        if (trimmed.Length == 0)
        {
            return (false, false);
        }

        // 只去掉空白与句末标点：正文里的一句话常常带逗号，标题截取时可以把它去掉，所以比较两边的「无标点」形式。
        var bare = new string(trimmed.Where(character => !char.IsWhiteSpace(character) && !Endings.Contains(character)).ToArray());
        var bareBody = new string(body.Where(character => !char.IsWhiteSpace(character)).ToArray());

        var fromBody = bare.Length > 0 && bareBody.Contains(bare, StringComparison.Ordinal);
        var joinsTwoThings = trimmed.Any(character => character is '，' or '、' or '和' or '与' || character is ',');

        return (fromBody, !fromBody && joinsTwoThings);
    }

    private const string Endings = "。！？!?";
}

/// <summary>
/// 形状判定的结果。数字与两个标题信号都留着，好写进日志与断言，而不是只给一个是非——判据调过之后要能看出
/// 它为什么判成这样。
/// </summary>
public sealed record ArticleStructureAssessment(
    bool LooksLikeInventory,
    int Paragraphs,
    int ParagraphsWithSources,
    int SingleSourceParagraphs,
    bool TitleFromBody,
    bool TitleLooksConstructed)
{
    /// <summary>是否该带着改写要求重来一次：流水账，或者标题是把两件事并起来的行程表。</summary>
    public bool NeedsRewrite => LooksLikeInventory || TitleLooksConstructed;
}
