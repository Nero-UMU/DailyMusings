namespace DailyMusings.Contracts;

/// <summary>
/// One instance's state in numbers, for the admin page's status area (docs/开发指导.md §16).
/// <para>
/// Read-only and aggregate by construction: there is no field here that could carry a transcript, a title or a
/// topic name. That is deliberate — a "statistics" endpoint is the easiest place in a product like this to leak
/// private material by accident, so the shape forbids it rather than relying on restraint.
/// </para>
/// </summary>
public sealed record StatisticsResponse(
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
    string? LastGenerationAtUtc,
    int QueuePending,
    int QueueRunning,
    int QueueFailed,
    int AudioRetentionDays,
    int ContentRetentionDays,
    bool SemanticSearchAvailable,
    bool GenerationEnabled,
    bool SmtpConfigured,
    long MediaBytes,
    long DatabaseBytes);

/// <summary>
/// One line of the instance's own log buffer (§16).
/// <para>
/// The message is whatever an existing call site already wrote, and every one of those obeys §16: identifiers,
/// states, durations and redacted codes, never a transcript, a prompt or a model response. Debug mode is the
/// documented exception and stays the operator's explicit decision.
/// </para>
/// </summary>
public sealed record LogEntryDto(string TimestampUtc, string Level, string Category, string Message);

public sealed record LogResponse(IReadOnlyList<LogEntryDto> Items, string MinimumLevel);
