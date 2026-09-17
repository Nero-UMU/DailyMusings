using DailyMusings.Client.Core;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Topics;

/// <summary>
/// The topic vocabulary as the client may use it (docs/开发指导.md §6.2, §9.3 主题：浏览、重命名、合并和调整归属).
/// <para>
/// A topic set the user cannot browse is a topic set they cannot trust, so this port does the three things that keep
/// it browsable — list what exists, rename it, and merge duplicates — plus filing one entry under topics by hand.
/// Automatic recognition never invents a topic; the only way a topic comes into existence is a person naming it.
/// </para>
/// </summary>
public interface ITopicApiClient
{
    /// <summary>
    /// The active topics. Merged topics are tombstones (A.9) and are not assignment targets, so they are left out
    /// unless an audit view asks for them.
    /// </summary>
    Task<ApiResult<IReadOnlyList<TopicDto>>> ListAsync(bool includeMerged, CancellationToken cancellationToken);

    Task<ApiResult<TopicDto>> CreateAsync(string name, CancellationToken cancellationToken);

    Task<ApiResult<TopicDto>> RenameAsync(string topicId, string name, CancellationToken cancellationToken);

    /// <summary>Merges one topic into another. Historical source maps are untouched (A.9).</summary>
    Task<ApiResult<TopicMergeResponse>> MergeAsync(
        string sourceTopicId,
        string targetTopicId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Files an entry under topics by hand: at most one primary, up to a few secondaries (§6.2). Passing a null
    /// primary clears the entry's primary topic without touching its other assignments.
    /// </summary>
    Task<ApiResult<InputTopicAssignmentResponse>> AssignAsync(
        string inputId,
        string? primaryTopicId,
        IReadOnlyList<string> secondaryTopicIds,
        CancellationToken cancellationToken);
}
