namespace DailyMusings.Contracts;

/// <summary>Ways an entry can be captured. Mirrors the domain's <c>InputSourceType</c> as a string on the wire.</summary>
public static class InputSourceNames
{
    public const string Voice = "voice";
    public const string Text = "text";
}

public static class TranscriptionStatusNames
{
    public const string NotApplicable = "notApplicable";
    public const string Pending = "pending";
    public const string InProgress = "inProgress";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class JobStatusNames
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// One captured entry plus the state of its transcription, which is what the client's timeline and capture
/// screens both render (docs/开发指导.md §9.1).
/// </summary>
public sealed record InputDto(
    string Id,
    string SourceType,
    string ContentDate,
    string CreatedAtUtc,
    int CreatedOffsetMinutes,
    string? OriginalTranscript,
    string? RevisedTranscript,
    string? Transcript,
    string TranscriptionStatus,
    string? FailureCode,
    bool HasAudio,
    string? AudioContentType,
    double? AudioDurationSeconds,
    bool IsDeleted,
    string? TranscriptionJobStatus,
    int TranscriptionJobAttempts);

public sealed record InputListResponse(IReadOnlyList<InputDto> Items);

/// <summary>
/// Text capture. <c>IdempotencyKey</c> is what makes a retried upload harmless; <c>CreatedAtUtc</c> and
/// <c>CreatedOffsetMinutes</c> carry the device's capture instant, which is what decides the content day (§7).
/// </summary>
public sealed record TextInputRequest(
    string Text,
    string? CreatedAtUtc,
    int? CreatedOffsetMinutes,
    string? IdempotencyKey);

/// <summary>Response to a capture. <c>AlreadyStored</c> tells a retrying client its first attempt landed.</summary>
public sealed record IngestResponse(bool AlreadyStored, InputDto Input);

public sealed record ReviseTranscriptRequest(string? RevisedTranscript);

public sealed record JobDto(
    string Id,
    string JobType,
    string TargetId,
    string Status,
    int AttemptCount,
    string ScheduledAtUtc,
    string? StartedAtUtc,
    string? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorSummary);

public sealed record JobListResponse(IReadOnlyList<JobDto> Items);

/// <summary>Form field names of the voice upload. Kept here so client and server cannot disagree.</summary>
public static class VoiceUploadFields
{
    public const string Audio = "audio";
    public const string CreatedAtUtc = "createdAtUtc";
    public const string CreatedOffsetMinutes = "createdOffsetMinutes";
    public const string DurationMilliseconds = "durationMilliseconds";
    public const string IdempotencyKey = "idempotencyKey";
}
