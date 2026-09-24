namespace DailyMusings.Application.Operations;

/// <summary>
/// The instance's state in numbers (docs/开发指导.md §16), as the read port returns it.
/// <para>
/// The shape is deliberately aggregate-only: there is no field here that could carry a transcript, a title or
/// a topic name. That is the point — a "statistics" read is the easiest place in a product like this to leak
/// private material by accident, so the type forbids it rather than relying on whoever writes the next query
/// to be careful.
/// </para>
/// <para>
/// It lives in the application layer rather than in the contracts assembly because this layer may only depend
/// on Domain (§5, enforced by a test); the API's own DTO is mapped from this at the host boundary, which is
/// where every other contract type is mapped too.
/// </para>
/// </summary>
public sealed record InstanceStatistics(
    string Today,
    int TodayInputCount,
    int TodayVoiceCount,
    int TodayTextCount,
    string TodayReflectionStatus,
    int TotalInputCount,
    int TotalReflectionCount,
    int ConfirmedReflectionCount,
    int DraftReflectionCount,
    int PublishedCount,
    int PendingPublicationCount,
    int ActiveDeviceCount,
    int RevokedDeviceCount,
    int TopicCount,
    DateTimeOffset? LastGenerationAtUtc,
    int QueuePending,
    int QueueRunning,
    int QueueFailed,
    int AudioRetentionDays,
    int ContentRetentionDays,
    bool SemanticSearchAvailable,
    bool GenerationEnabled,
    bool SmtpConfigured,
    long MediaBytes,
    long DatabaseBytes)
{
    /// <summary>
    /// What <see cref="TodayReflectionStatus"/> says when the day has no draft yet. A distinct value rather
    /// than an empty string, because "nothing has been generated" is a state the admin page has to render, not
    /// a missing value.
    /// </summary>
    public const string NoReflection = "none";
}

/// <summary>
/// Reads the instance's counters (docs/开发指导.md §16).
/// <para>
/// One port implemented directly in SQL rather than a dozen <c>Count</c> methods spread over every repository:
/// these are read-only aggregates over tables that already exist, they are all needed at the same moment for
/// the same screen, and giving each one a home in the repository it touches would add a lot of interface for
/// no invariant.
/// </para>
/// </summary>
public interface IStatisticsReader
{
    Task<InstanceStatistics> ReadAsync(CancellationToken cancellationToken);
}
