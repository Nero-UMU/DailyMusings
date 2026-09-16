using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Topics;

/// <summary>
/// A theme that inputs are filed under (docs/开发指导.md §6.2).
/// <para>
/// Merge is modelled as a tombstone rather than a delete (decision A.9): the row survives with
/// <see cref="MergedIntoId"/> set. That keeps client caches and historical references from breaking,
/// and removes any chance of a retired identifier being reused with a different meaning later.
/// </para>
/// </summary>
public sealed class Topic
{
    private Topic(TopicId id, string name, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Stable and immutable for the lifetime of the topic.</summary>
    public TopicId Id { get; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public TopicId? MergedIntoId { get; private set; }

    public DateTimeOffset? MergedAtUtc { get; private set; }

    public bool IsMerged => MergedIntoId is not null;

    public static Topic Create(TopicId id, string name, DateTimeOffset createdAtUtc)
    {
        ValidateName(name);
        return new Topic(id, name.Trim(), createdAtUtc);
    }

    /// <summary>
    /// Rehydrates a persisted topic, including a merge tombstone. The name is validated exactly as on creation:
    /// a topic that could not have been created should not appear to exist.
    /// </summary>
    public static Topic Rehydrate(
        TopicId id,
        string name,
        DateTimeOffset createdAtUtc,
        TopicId? mergedIntoId,
        DateTimeOffset? mergedAtUtc)
    {
        var topic = Create(id, name, createdAtUtc);

        if (mergedIntoId is { IsEmpty: false } target)
        {
            topic.MergedIntoId = target;
            topic.MergedAtUtc = mergedAtUtc;
        }

        return topic;
    }

    /// <summary>Renaming changes the display label only — never <see cref="Id"/>.</summary>
    public void Rename(string name)
    {
        EnsureActive();
        ValidateName(name);
        Name = name.Trim();
    }

    /// <summary>
    /// Retires this topic in favour of <paramref name="target"/>. Callers must also re-point every
    /// input that referenced this topic; <see cref="Inputs.InputEntry.RemapTopic"/> does that.
    /// </summary>
    public void MergeInto(TopicId target, DateTimeOffset at)
    {
        EnsureActive();

        if (target == Id)
        {
            throw new DomainException("topic.merge.self", "A topic cannot be merged into itself.");
        }

        if (target.IsEmpty)
        {
            throw new DomainException("topic.merge.target_empty", "A merge target is required.");
        }

        MergedIntoId = target;
        MergedAtUtc = at;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length > 128)
        {
            throw new DomainException("topic.name.too_long", "A topic name may not exceed 128 characters.");
        }
    }

    private void EnsureActive()
    {
        if (IsMerged)
        {
            throw new DomainException(
                "topic.merged",
                $"The topic was merged into {MergedIntoId} and can no longer be modified.");
        }
    }
}
