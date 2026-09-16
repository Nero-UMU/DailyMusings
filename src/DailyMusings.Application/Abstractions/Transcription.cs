namespace DailyMusings.Application.Abstractions;

/// <summary>
/// One transcription request. The audio is supplied as an opener rather than a stream so the adapter stays
/// ignorant of blob storage: it asks for the bytes when it is ready to send them.
/// </summary>
public sealed record TranscriptionRequest(
    Func<CancellationToken, Task<Stream>> OpenAudio,
    string FileName,
    string ContentType,
    string? LanguageHint = null);

public sealed record TranscriptionResult(string Text, string? DetectedLanguage, string? ModelName);

/// <summary>
/// Speech-to-text against a user-supplied, OpenAI-compatible endpoint (docs/开发指导.md §8.1). The project
/// bundles no model: this port is the only place that knows how to talk to one, and it must never log the audio
/// or the resulting text (§16).
/// </summary>
public interface ITranscriptionClient
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The transcription endpoint's configuration. Secrets are referenced by <em>name</em> only — the value is
/// resolved from Docker secrets at call time and never stored here (§10.4).
/// </summary>
public sealed record TranscriptionSettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    string SecretName,
    TimeSpan Timeout,
    string? LanguageHint)
{
    /// <summary>Disabled until an operator configures an endpoint. Nothing is sent anywhere by default.</summary>
    public static TranscriptionSettings Default { get; } = new(
        Enabled: false,
        BaseUrl: "https://api.openai.com/v1",
        Model: "whisper-1",
        SecretName: "openai-api-key",
        Timeout: TimeSpan.FromMinutes(2),
        LanguageHint: null);
}

public interface ITranscriptionSettingsProvider
{
    Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A failure that is worth retrying later (network, rate limit, 5xx) as opposed to one that is not (bad
/// credentials, unsupported audio). §14 retries transient errors with backoff and stops for permanent ones.
/// </summary>
public sealed class TransientExternalFailureException : Exception
{
    public TransientExternalFailureException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>A failure that will not succeed on retry, so the job should fail immediately.</summary>
public sealed class PermanentExternalFailureException : Exception
{
    public PermanentExternalFailureException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
