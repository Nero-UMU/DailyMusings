using System.Text.Json.Serialization;

namespace DailyMusings.Client.Core;

public enum CaptureKind
{
    Voice = 0,
    Text = 1,
}

/// <summary>
/// Where a capture stands relative to the server (docs/开发指导.md §9.2).
/// <para>
/// <see cref="Uploaded"/> means the server has confirmed it holds the capture. It no longer means the local copy
/// may be deleted: the phone is the source of truth for 往期记录, so the recording and its recognised text stay on
/// the device after a successful upload (the phone's own 2026-09-24 scope: 录音、看识别文字、回看往期).
/// </para>
/// </summary>
public enum CaptureUploadState
{
    /// <summary>Durable on the device, not yet confirmed by the server.</summary>
    Queued = 0,

    /// <summary>The server confirmed it. The local copy is kept.</summary>
    Uploaded = 1,

    /// <summary>The last attempt failed. The local copy is kept and the reason is recorded.</summary>
    Failed = 2,
}

/// <summary>
/// One thought on the device: either a recording with the text the server recognised, or a typed note. Immutable,
/// so every state change is an explicit new value rather than a mutation some background loop might race with.
/// <para>
/// This is also the local archive the calendar reads, which is why <see cref="Transcript"/> lives here rather than
/// being re-fetched from the server every time the user wants to read what they said.
/// </para>
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

    /// <summary>The recording on this device. Kept after a successful upload — it is the local archive's audio.</summary>
    public string? LocalAudioPath { get; init; }

    /// <summary>What the user typed. For a typed note this is also what the server stores as the transcript.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// What the server recognised (voice) or accepted (text). Null while a recording is still being transcribed.
    /// </summary>
    public string? Transcript { get; init; }

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

    /// <summary>When the recognised text arrived from the server; null while there is no text yet.</summary>
    public DateTimeOffset? TranscriptionAtUtc { get; init; }

    /// <summary>
    /// True while the server has not confirmed this capture. What the upload loop walks, and what the UI means by
    /// 「未上传」. Replaces the old <c>HoldsOnlyCopy</c>: the device now always holds a copy, sent or not.
    /// </summary>
    [JsonIgnore]
    public bool NeedsUpload => State != CaptureUploadState.Uploaded;

    /// <summary>What to show the user: the recognised text, or what they typed while there is none.</summary>
    [JsonIgnore]
    public string? DisplayText => Transcript ?? Text;

    [JsonIgnore]
    public bool IsVoice => Kind == CaptureKind.Voice;
}
