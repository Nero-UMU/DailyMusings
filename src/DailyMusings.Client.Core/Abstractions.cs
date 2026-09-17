using DailyMusings.Contracts;

namespace DailyMusings.Client.Core;

/// <summary>A clock the tests own, so capture timestamps are exact rather than approximately now.</summary>
public interface IClientClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>The device's current UTC offset, recorded with each capture.</summary>
    int LocalOffsetMinutes { get; }
}

/// <summary>
/// Durable local storage for captures that have not been confirmed by the server yet.
/// <para>
/// Everything here must survive the process being killed: §9.2 requires the client to write to safe storage
/// <em>before</em> it tells the user the recording is done.
/// </para>
/// </summary>
public interface IOfflineCaptureStore
{
    /// <summary>
    /// Persists audio bytes and returns the local path. Returns only once the bytes are durable — a buffered write
    /// that a power loss could erase does not satisfy §9.2.
    /// </summary>
    Task<string> SaveAudioAsync(
        string captureId,
        Stream content,
        string fileExtension,
        CancellationToken cancellationToken);

    Task<PendingCapture?> FindAsync(string captureId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the record.</summary>
    Task UpsertAsync(PendingCapture capture, CancellationToken cancellationToken);

    /// <summary>Removes the record and its local audio. Called only after the server confirmed the capture.</summary>
    Task DeleteAsync(string captureId, CancellationToken cancellationToken);
}

public sealed record VoiceUpload(
    string LocalAudioPath,
    string ContentType,
    double? DurationSeconds,
    string IdempotencyKey,
    DateTimeOffset CreatedAtUtc,
    int CreatedOffsetMinutes);

public sealed record TextUpload(
    string Text,
    string IdempotencyKey,
    DateTimeOffset CreatedAtUtc,
    int CreatedOffsetMinutes);

/// <summary>
/// Supplies the paired device token. The value lives in the platform's secure storage (§10.2), so this port is
/// how the queue reaches it without knowing which platform it is on.
/// </summary>
public interface IDeviceTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>The server-facing operations the queue needs. Returns the server's own response type verbatim.</summary>
public interface ICaptureApiClient
{
    Task<IngestResponse> UploadVoiceAsync(VoiceUpload upload, CancellationToken cancellationToken);

    Task<IngestResponse> UploadTextAsync(TextUpload upload, CancellationToken cancellationToken);

    /// <summary>Reads an entry back, so the UI can show transcription progress after the upload.</summary>
    Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads entries. Returns a result rather than throwing, so the screen can say "连不上服务器" instead of showing
    /// an empty day: an empty list and an unreachable server look identical to a user, and §9.2's whole premise is
    /// that the client works with the server gone.
    /// </summary>
    /// <param name="contentDate">
    /// One day, or <c>null</c> for the most recent entries across days — which is what a month view needs, so that
    /// drawing it costs one request instead of one per day.
    /// </param>
    Task<ApiResult<IReadOnlyList<InputDto>>> GetInputsAsync(string? contentDate, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the user's correction to a transcript (§4.1 修订转写). The original is kept beside it — §6.1 keeps both,
    /// because a correction is a decision the user may want to revisit.
    /// </summary>
    Task<ApiResult<InputDto>> ReviseTranscriptAsync(
        string inputId,
        string? revisedTranscript,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks the server to transcribe an entry again (§9.2's manual retry, for the transcription rather than the
    /// upload). Only useful when the first attempt failed — the server refuses otherwise.
    /// </summary>
    Task<ApiResult<InputDto>> RetryTranscriptionAsync(string inputId, CancellationToken cancellationToken);
}



/// <summary>
/// An upload failure, classified the way the retry policy needs it. <see cref="Transient"/> means "try again
/// later"; a non-transient failure needs the user or the configuration to change first.
/// </summary>
public sealed class CaptureUploadException : Exception
{
    public CaptureUploadException(string code, string message, bool transient, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Transient = transient;
    }

    public string Code { get; }

    public bool Transient { get; }
}

/// <summary>
/// Records short audio. Implemented per platform: it is the one piece of the capture path that cannot be written
/// once for every OS.
/// </summary>
public interface IAudioRecorder
{
    bool IsRecording { get; }

    /// <summary>Begins capturing. The file is written under the platform's app data directory.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops capturing and returns the finished recording. The returned stream must be readable to its end; the
    /// caller copies it into durable storage before telling the user anything.
    /// </summary>
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed record RecordedAudio(Stream Content, string FileExtension, string ContentType, double DurationSeconds);
