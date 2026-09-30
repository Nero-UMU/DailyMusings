using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Application.Topics;

/// <summary>
/// The topic vocabulary (docs/开发指导.md §6.2, §13). Topics are the fallback retrieval signal, so they have to
/// keep working when the embedding index does not — which is why they are plain rows the user owns rather than
/// anything derived from a model.
/// </summary>
public sealed class ListTopicsUseCase
{
    private readonly ITopicRepository _topics;

    public ListTopicsUseCase(ITopicRepository topics) => _topics = topics;

    /// <param name="includeMerged">
    /// Merged topics are tombstones (A.9). They are returned only for an audit view: the client must not offer
    /// them as an assignment target.
    /// </param>
    public Task<IReadOnlyList<Topic>> ExecuteAsync(bool includeMerged, CancellationToken cancellationToken) =>
        _topics.ListAsync(includeMerged, cancellationToken);
}

/// <summary>
/// Creates a topic.
/// <para>
/// §13 does not list a create endpoint, but the feature is unusable without one: automatic recognition only
/// files material under topics that already exist, precisely so the vocabulary stays one the user recognizes.
/// Creating by name is idempotent — asking for a name that already exists returns the existing topic instead of
/// producing a second one that differs only in spacing or punctuation.
/// </para>
/// </summary>
public sealed class CreateTopicUseCase
{
    private readonly ITopicRepository _topics;
    private readonly IClock _clock;

    public CreateTopicUseCase(ITopicRepository topics, IClock clock)
    {
        _topics = topics;
        _clock = clock;
    }

    public async Task<(Topic Topic, bool Created)> ExecuteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = await _topics.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, false);
        }

        var topic = Topic.Create(TopicId.New(), name, _clock.UtcNow);
        await _topics.AddAsync(topic, cancellationToken).ConfigureAwait(false);
        return (topic, true);
    }
}

/// <summary>Renames a topic. Only the display name changes — the id is stable (A.9).</summary>
public sealed class RenameTopicUseCase
{
    private readonly ITopicRepository _topics;

    public RenameTopicUseCase(ITopicRepository topics) => _topics = topics;

    public async Task<Topic> ExecuteAsync(TopicId topicId, string name, CancellationToken cancellationToken)
    {
        var topic = await RequireTopicAsync(_topics, topicId, cancellationToken).ConfigureAwait(false);

        topic.Rename(name);
        await _topics.UpdateAsync(topic, cancellationToken).ConfigureAwait(false);
        return topic;
    }

    internal static async Task<Topic> RequireTopicAsync(
        ITopicRepository topics,
        TopicId topicId,
        CancellationToken cancellationToken) =>
        await topics.FindByIdAsync(topicId, cancellationToken).ConfigureAwait(false)
        ?? throw new UseCaseException("topic.unknown", $"No topic with id {topicId}.");
}

/// <summary>
/// Merges one topic into another (docs/开发指导.md §6.2, decision A.9).
/// <para>
/// The source row survives as a tombstone and every input filed under it is re-pointed in the same
/// transaction. What is deliberately absent from this operation is any change to the source map: §6.5 keeps
/// <c>SourceReference</c> pointing at inputs, so merging topics can never move a historical article's
/// citations. §17.1 asks for that to be a fixed invariant rather than an accident.
/// </para>
/// </summary>
public sealed class MergeTopicsUseCase
{
    private readonly ITopicRepository _topics;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public MergeTopicsUseCase(ITopicRepository topics, IUnitOfWork unitOfWork, IClock clock)
    {
        _topics = topics;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<(Topic Source, Topic Target, int RemappedInputs)> ExecuteAsync(
        TopicId sourceId,
        TopicId targetId,
        CancellationToken cancellationToken)
    {
        if (sourceId == targetId)
        {
            throw new UseCaseException("topic.merge.self", "主题不能合并到它自己。");
        }

        var source = await RenameTopicUseCase.RequireTopicAsync(_topics, sourceId, cancellationToken).ConfigureAwait(false);
        var target = await RenameTopicUseCase.RequireTopicAsync(_topics, targetId, cancellationToken).ConfigureAwait(false);

        if (target.IsMerged)
        {
            // Merging into a tombstone would leave the material pointing at a topic that no list shows.
            throw new UseCaseException(
                "topic.merge.target_retired",
                "目标主题自己已经被合并走了，不能再接收内容。");
        }

        var now = _clock.UtcNow;

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        var remapped = await _topics.RemapInputsAsync(source.Id, target.Id, cancellationToken).ConfigureAwait(false);

        source.MergeInto(target.Id, now);
        await _topics.UpdateAsync(source, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (source, target, remapped);
    }
}

/// <summary>
/// Files an input under topics the user chose (§13 PUT /api/inputs/{id}/topics).
/// </summary>
public sealed class AssignInputTopicsUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly ITopicRepository _topics;

    public AssignInputTopicsUseCase(IInputEntryRepository inputs, ITopicRepository topics)
    {
        _inputs = inputs;
        _topics = topics;
    }

    public async Task<InputEntry> ExecuteAsync(
        InputEntryId inputId,
        TopicId? primaryTopicId,
        IReadOnlyList<TopicId> secondaryTopicIds,
        CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(inputId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("input.unknown", $"No input with id {inputId}.");

        // Every referenced topic must exist and still be active: a merged topic is a tombstone, and filing new
        // material under it would resurrect it in every list the user sees (A.9).
        foreach (var topicId in secondaryTopicIds.Concat(primaryTopicId is { } primary ? [primary] : []))
        {
            var topic = await RenameTopicUseCase.RequireTopicAsync(_topics, topicId, cancellationToken).ConfigureAwait(false);
            if (topic.IsMerged)
            {
                throw new UseCaseException(
                    "topic.retired",
                    $"Topic {topicId} was merged into {topic.MergedIntoId} and cannot receive new material.");
            }
        }

        entry.AssignTopics(primaryTopicId, secondaryTopicIds);
        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

        return entry;
    }
}

/// <summary>
/// Recognises which existing topics an input belongs to (docs/开发指导.md §8.2 step 5).
/// <para>
/// Runs right after transcription, and only when the input has no topics yet — a later retry of the
/// transcription must not overwrite a decision the user made by hand.
/// </para>
/// </summary>
public sealed class AssignTopicsAutomaticallyUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly ITopicRepository _topics;

    public AssignTopicsAutomaticallyUseCase(IInputEntryRepository inputs, ITopicRepository topics)
    {
        _inputs = inputs;
        _topics = topics;
    }

    /// <returns>The assignment that was made, or <c>null</c> when nothing was recognized.</returns>
    public async Task<(TopicId? Primary, IReadOnlyList<TopicId> Secondary)?> ExecuteAsync(
        InputEntryId inputId,
        CancellationToken cancellationToken)
    {
        var entry = await _inputs.FindByIdAsync(inputId, cancellationToken).ConfigureAwait(false);

        if (entry is null || entry.IsDeleted)
        {
            return null;
        }

        if (entry.PrimaryTopicId is not null || entry.SecondaryTopicIds.Count > 0)
        {
            return null; // already filed, by the user or by an earlier run
        }

        var text = entry.TranscriptForGeneration;
        var topics = await _topics.ListAsync(includeMerged: false, cancellationToken).ConfigureAwait(false);
        if (topics.Count == 0)
        {
            return null;
        }

        var (primary, secondary) = TopicMatcher.ToAssignment(TopicMatcher.Suggest(text, topics));
        if (primary is null && secondary.Count == 0)
        {
            return null;
        }

        entry.AssignTopics(primary, secondary);
        await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

        return (primary, secondary);
    }
}
