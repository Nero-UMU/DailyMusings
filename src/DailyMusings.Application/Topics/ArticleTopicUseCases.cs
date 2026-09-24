using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Application.Topics;

/// <summary>
/// Decides which topics a freshly generated article is about (docs/开发指导.md §6.2 as revised).
/// <para>
/// The model proposes <em>names</em>, never identifiers: asking it to echo UUIDs invites invented ones. This use
/// case turns those names into real topics — reusing an existing one whenever the name matches, and creating a
/// new one, marked <see cref="TopicOrigin.Model"/>, only when the vocabulary genuinely has nothing that fits.
/// The origin is recorded rather than hidden because the user has to be able to tell which names they chose and
/// which ones a model proposed on their behalf.
/// </para>
/// <para>
/// It never invents a topic the model did not ask for. An empty answer is a legitimate answer: an article whose
/// day fits nothing already known, and whose model proposed nothing, simply has no topics, and the admin page
/// says so. Filling that gap with a guess would put a claim in the database that nobody made.
/// </para>
/// </summary>
public sealed class ResolveArticleTopicsUseCase
{
    /// <summary>
    /// How many topics one article may carry. Bounded because §6.2's vocabulary is meant to be browsable, and a
    /// model told "list topics" will happily produce ten for one paragraph.
    /// </summary>
    public const int MaxTopicsPerArticle = 3;

    private readonly ITopicRepository _topics;
    private readonly IClock _clock;

    public ResolveArticleTopicsUseCase(ITopicRepository topics, IClock clock)
    {
        _topics = topics;
        _clock = clock;
    }

    /// <param name="modelTopicNames">
    /// Names as the model wrote them — reused names and newly proposed ones mixed together, because the model is
    /// not asked to distinguish them and reads better when it does not have to.
    /// </param>
    /// <returns>The primary topic first, then the secondaries. Both are empty when nothing resolved.</returns>
    public async Task<(TopicId? Primary, IReadOnlyList<TopicId> Secondary)> ExecuteAsync(
        IEnumerable<string>? modelTopicNames,
        CancellationToken cancellationToken)
    {
        var ordered = new List<TopicId>();

        foreach (var name in Clean(modelTopicNames))
        {
            if (ordered.Count >= MaxTopicsPerArticle)
            {
                break;
            }

            // Matching normalizes case, spacing and punctuation, which is the same comparison the automatic
            // input filing uses: "录音 上传" and "录音、上传" are one topic, not two. A name that was merged away
            // resolves to what it was merged into, so a merge is not undone by the next generation.
            var topic = await _topics.FindActiveByNameFollowingMergesAsync(name, cancellationToken).ConfigureAwait(false);

            topic ??= await CreateModelTopicAsync(name, cancellationToken).ConfigureAwait(false);

            if (topic is not null && !ordered.Contains(topic.Id))
            {
                ordered.Add(topic.Id);
            }
        }

        return ordered.Count == 0
            ? (null, [])
            : (ordered[0], ordered.Skip(1).ToArray());
    }

    private async Task<Topic?> CreateModelTopicAsync(string name, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();

        // A name that is too long or too short to be a topic is dropped rather than truncated: a truncated name
        // is a different word, and the user would have to guess what it used to say.
        if (trimmed.Length is 0 or > 32)
        {
            return null;
        }

        // A second read after a rejected write would be needed to handle a lost race; SQLite here is single
        // writer and this runs inside the generation job, so the simple form is the honest one.
        var topic = Topic.Create(TopicId.New(), trimmed, _clock.UtcNow, TopicOrigin.Model);
        await _topics.AddAsync(topic, cancellationToken).ConfigureAwait(false);

        return topic;
    }

    private static IEnumerable<string> Clean(IEnumerable<string>? names) =>
        (names ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(name => TopicMatcher.Normalize(name).Length > 0)
            .Distinct(StringComparer.Ordinal);
}

/// <summary>A topic with the two counts the admin list shows next to it.</summary>
public sealed record TopicSummary(Topic Topic, int ArticleCount, int InputCount);

/// <summary>The topics plus how much is using them (docs/开发指导.md §6.2).</summary>
public sealed class ListTopicsWithUsageUseCase
{
    private readonly ITopicRepository _topics;

    public ListTopicsWithUsageUseCase(ITopicRepository topics) => _topics = topics;

    public async Task<IReadOnlyList<TopicSummary>> ExecuteAsync(
        bool includeMerged,
        CancellationToken cancellationToken)
    {
        var topics = await _topics.ListAsync(includeMerged, cancellationToken).ConfigureAwait(false);
        var summaries = new List<TopicSummary>(topics.Count);

        foreach (var topic in topics)
        {
            summaries.Add(new TopicSummary(
                topic,
                await _topics.CountArticleUsagesAsync(topic.Id, cancellationToken).ConfigureAwait(false),
                await _topics.CountInputUsagesAsync(topic.Id, cancellationToken).ConfigureAwait(false)));
        }

        return summaries;
    }
}

/// <summary>Which articles are holding one topic in use — the list that tells the user what to re-file first.</summary>
public sealed record TopicUsageView(
    Topic Topic,
    IReadOnlyList<TopicArticleUsage> Articles,
    int InputCount);

public sealed class GetTopicUsageUseCase
{
    private readonly ITopicRepository _topics;

    public GetTopicUsageUseCase(ITopicRepository topics) => _topics = topics;

    public async Task<TopicUsageView> ExecuteAsync(TopicId topicId, CancellationToken cancellationToken)
    {
        var topic = await RenameTopicUseCase.RequireTopicAsync(_topics, topicId, cancellationToken).ConfigureAwait(false);

        return new TopicUsageView(
            topic,
            await _topics.ListArticleUsagesAsync(topicId, cancellationToken).ConfigureAwait(false),
            await _topics.CountInputUsagesAsync(topicId, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Deletes a topic (docs/开发指导.md §6.2).
/// <para>
/// Two rules, and the first one is the reason this is not a one-line repository call. A topic an article is
/// <em>currently</em> about cannot be deleted: the article's topics are a statement about what that day was
/// about, and deleting the label would silently rewrite it — so the user is told to move the articles first,
/// which is exactly what the admin page's 内容管理 offers. "Currently" means the working or confirmed version,
/// which is what that page shows and re-files; versions a day has moved on from are not a reason to keep a label,
/// and counting them would make a topic referenced only by the permanently retained first version undeletable.
/// </para>
/// <para>
/// Second, deletion never touches the material or the writing. Inputs are unfiled (primary cleared, secondary
/// rows dropped), the topic's links on every version are removed, and the entries and article bodies stay exactly
/// as they were — §6.2 forbids a deletion from costing the user their own words.
/// </para>
/// </summary>
public sealed class DeleteTopicUseCase
{
    private readonly ITopicRepository _topics;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteTopicUseCase(ITopicRepository topics, IUnitOfWork unitOfWork)
    {
        _topics = topics;
        _unitOfWork = unitOfWork;
    }

    public async Task<Topic> ExecuteAsync(TopicId topicId, CancellationToken cancellationToken)
    {
        var topic = await RenameTopicUseCase.RequireTopicAsync(_topics, topicId, cancellationToken).ConfigureAwait(false);

        // A tombstone points here. Deleting the target would leave that record of "this was merged into X"
        // naming a topic that no longer exists — the exact dangling reference decision A.9 keeps tombstones to
        // avoid. Checking in memory is right for this table: the vocabulary is a handful of rows a person
        // curates, and the alternative is a second repository query that would say the same thing.
        var all = await _topics.ListAsync(includeMerged: true, cancellationToken).ConfigureAwait(false);

        if (all.Any(candidate => candidate.MergedIntoId == topicId))
        {
            throw new UseCaseException(
                "topic.merge_target",
                "还有主题被合并到了这个主题上，删除会让那条合并记录失去目标；请先处理那些合并。");
        }

        // One transaction around the guard and the delete: checking in one statement and deleting in another
        // would let an article be filed under the topic in between, and the guard would have been true when it
        // was read and false when it mattered.
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (await _topics.CountArticleUsagesAsync(topicId, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new UseCaseException(
                "topic.in_use",
                "还有文章在用这个主题，请先在「内容管理」里把这些文章迁移到别的主题。");
        }

        await _topics.ClearInputAssignmentsAsync(topicId, cancellationToken).ConfigureAwait(false);

        // The article-side links go too, including the ones on versions a day has moved on from. Without this the
        // foreign key would refuse the deletion — and refusing is the wrong answer here: the user asked for the
        // label to disappear, and a retired version carrying it is not an article that is "about" it.
        await _topics.ClearVersionTopicLinksAsync(topicId, cancellationToken).ConfigureAwait(false);

        await _topics.DeleteAsync(topicId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return topic;
    }
}

/// <summary>
/// Re-files the working version of a day's article (the migration path the deletion guard sends the user to).
/// <para>
/// It addresses the current working version on purpose: the topics are a property of the text the user is
/// looking at, and re-filing an older version would change a snapshot that is kept precisely because it is what
/// it was.
/// </para>
/// </summary>
public sealed class AssignReflectionVersionTopicsUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly ITopicRepository _topics;
    private readonly IUnitOfWork _unitOfWork;

    public AssignReflectionVersionTopicsUseCase(
        IReflectionRepository reflections,
        ITopicRepository topics,
        IUnitOfWork unitOfWork)
    {
        _reflections = reflections;
        _topics = topics;
        _unitOfWork = unitOfWork;
    }

    public async Task<ReflectionVersion> ExecuteAsync(
        ContentDate contentDate,
        TopicId? primaryTopicId,
        IReadOnlyList<TopicId> secondaryTopicIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secondaryTopicIds);

        var reflection = await _reflections
            .FindByContentDateAsync(contentDate, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.unknown", $"No reflection exists for {contentDate}.");

        if (reflection.WorkingVersionId is not { } versionId)
        {
            throw new UseCaseException("reflection.version.unknown", "This day has no working version to re-file.");
        }

        // Every referenced topic has to exist and still be active. Filing an article under a tombstone would
        // resurrect it in every list the user sees, which is the one thing a merge promises never to happen (A.9).
        foreach (var topicId in secondaryTopicIds.Concat(primaryTopicId is { } primary ? [primary] : []))
        {
            var topic = await RenameTopicUseCase.RequireTopicAsync(_topics, topicId, cancellationToken)
                .ConfigureAwait(false);

            if (topic.IsMerged)
            {
                throw new UseCaseException(
                    "topic.retired",
                    $"Topic {topicId} was merged into {topic.MergedIntoId} and cannot receive new material.");
            }
        }

        var ordered = new List<TopicId>();

        if (primaryTopicId is { IsEmpty: false } main)
        {
            ordered.Add(main);
        }

        ordered.AddRange(secondaryTopicIds.Where(topicId => !topicId.IsEmpty && topicId != primaryTopicId));

        // One transaction around the replacement: the delete and the inserts are a single statement in intent —
        // an interruption between them would leave the article with no topics at all, which reads as "nobody ever
        // decided this" rather than "the write did not happen".
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        await _reflections.SetVersionTopicsAsync(versionId, ordered, cancellationToken).ConfigureAwait(false);

        var updated = await _reflections.FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.version.unknown", "This day has no working version to re-file.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return updated;
    }
}
