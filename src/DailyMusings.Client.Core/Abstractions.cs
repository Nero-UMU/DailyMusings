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
/// Durable local storage for the device's own archive of thoughts.
/// <para>
/// Everything here must survive the process being killed: §9.2 requires the client to write to safe storage
/// <em>before</em> it tells the user the recording is done. The store is the archive, not a queue that empties
/// itself — a capture the server has confirmed stays on the device until the user deletes it.
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

    /// <summary>Every record on the device, newest first.</summary>
    Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the record.</summary>
    Task UpsertAsync(PendingCapture capture, CancellationToken cancellationToken);

    /// <summary>Removes the record and its local audio. Called when the user deletes a capture, never as a side
    /// effect of a successful upload.</summary>
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

/// <summary>The server-facing operations the phone needs. Returns the server's own response type verbatim.</summary>
public interface ICaptureApiClient
{
    Task<IngestResponse> UploadVoiceAsync(VoiceUpload upload, CancellationToken cancellationToken);

    Task<IngestResponse> UploadTextAsync(TextUpload upload, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one entry back, so the phone can fill in a transcript that was still being produced when the upload
    /// answered (docs/开发指导.md §8.2, and the phone spec's 0.1: the voice upload transcribes inline by default,
    /// and a pending answer is completed by a few reads of this route).
    /// </summary>
    Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads recent entries, newest first. The calendar itself reads the <em>device's</em> records — this is here so
    /// a recording whose transcription had not finished can have its text filled in without one request per entry.
    /// Returns a result rather than throwing, so a screen can say "连不上服务器" instead of showing an empty list:
    /// an empty list and an unreachable server look identical to a user, and §9.2's whole premise is that the
    /// client works with the server gone.
    /// </summary>
    Task<ApiResult<IReadOnlyList<InputDto>>> GetInputsAsync(string? contentDate, CancellationToken cancellationToken);
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
