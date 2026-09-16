namespace DailyMusings.Client.Core;

public enum CaptureKind
{
    Voice = 0,
    Text = 1,
}

/// <summary>
/// Where a capture stands relative to the server (docs/开发指导.md §9.2).
/// <para>
/// The distinction that matters is <see cref="Uploaded"/>: only a capture the server has confirmed as durable may
/// have its local copy deleted. Until then the device is the only place the thought exists.
/// </para>
/// </summary>
public enum CaptureUploadState
{
    /// <summary>Durable on the device, not yet confirmed by the server.</summary>
    Queued = 0,

    /// <summary>The server confirmed it. The local copy has been (or may be) removed.</summary>
    Uploaded = 1,

    /// <summary>The last attempt failed. The local copy is kept and the reason is recorded.</summary>
    Failed = 2,
}

/// <summary>
/// One capture on its way to the server. Immutable, so every state change is an explicit new value rather than a
/// mutation some background loop might race with.
/// </summary>
public sealed record PendingCapture
{
    /// <summary>Local identifier, used for file naming and for the UI to address the row.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Sent to the server with every attempt and never regenerated. This is what makes a retry harmless: the
    /// server recognises the key and returns the original entry instead of creating a second one.
    /// </summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    public CaptureKind Kind { get; init; }

    public string? LocalAudioPath { get; init; }

    public string? Text { get; init; }

    /// <summary>When the user captured it, not when it was uploaded — this is what decides the content day (§7).</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>The device's UTC offset at capture time, recorded for traceability.</summary>
    public int CreatedOffsetMinutes { get; init; }

    public double? DurationSeconds { get; init; }

    public CaptureUploadState State { get; init; }

    public int AttemptCount { get; init; }

    /// <summary>Stable code from the last failure, suitable for showing next to the row (§9.2).</summary>
    public string? FailureCode { get; init; }

    public string? FailureSummary { get; init; }

    /// <summary>The entry the server created, once known.</summary>
    public string? ServerInputId { get; init; }

    public DateTimeOffset? UploadedAtUtc { get; init; }

    /// <summary>True while the device still holds the only copy.</summary>
    public bool HoldsOnlyCopy => State != CaptureUploadState.Uploaded;
}
