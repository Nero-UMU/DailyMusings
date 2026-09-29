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
/// Speech-to-text against a user-supplied endpoint (docs/开发指导.md §8.1). The project bundles no model:
/// this port stays independent of the selected provider protocol, and it must never log the audio or the
/// resulting text (§16).
/// </summary>
public interface ITranscriptionClient
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken);
}

/// <summary>Stable names persisted by the admin page for the supported speech-to-text wire protocols.</summary>
public static class TranscriptionProtocolNames
{
    public const string Auto = "auto";
    public const string OpenAiAudioTranscriptions = "openai-audio-transcriptions";
    public const string QwenAsrChat = "qwen-asr-chat";
    public const string DashScopeMultimodal = "dashscope-multimodal";

    public static bool IsSupported(string? value) => value is
        Auto or OpenAiAudioTranscriptions or QwenAsrChat or DashScopeMultimodal;

    /// <summary>
    /// Resolves the one protocol used for a request. Automatic selection is deliberately concentrated here so
    /// saving, testing and actual transcription cannot disagree about a model-name heuristic.
    /// </summary>
    public static string Resolve(string? configured, string model)
    {
        var selected = string.IsNullOrWhiteSpace(configured) ? Auto : configured.Trim().ToLowerInvariant();
        if (selected != Auto)
        {
            return selected;
        }

        if (model.StartsWith("qwen3-asr-flash", StringComparison.OrdinalIgnoreCase))
        {
            return QwenAsrChat;
        }

        if (model.StartsWith("qwen-audio-3.", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("fun-asr-flash", StringComparison.OrdinalIgnoreCase))
        {
            return DashScopeMultimodal;
        }

        return OpenAiAudioTranscriptions;
    }

    public static bool RequiresUnsupportedTransport(string model) =>
        model.StartsWith("paraformer", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("filetrans", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("realtime", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("streaming", StringComparison.OrdinalIgnoreCase) ||
        (model.StartsWith("fun-asr", StringComparison.OrdinalIgnoreCase) &&
         !model.StartsWith("fun-asr-flash", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Optional transcription knobs shared by configuration, the candidate connection probe and the production
/// adapter. Unsupported knobs are simply not sent by a protocol; the UI explains which ones apply.
/// </summary>
public sealed record TranscriptionParameters(
    string Protocol,
    string? LanguageHints,
    bool EnableItn,
    string? VocabularyId,
    bool SpeakerDiarization,
    bool KeepDialect)
{
    public static TranscriptionParameters Default { get; } = new(
        TranscriptionProtocolNames.Auto,
        LanguageHints: null,
        EnableItn: false,
        VocabularyId: null,
        SpeakerDiarization: false,
        KeepDialect: false);
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
    TranscriptionParameters Parameters)
{
    /// <summary>Disabled until an operator configures an endpoint. Nothing is sent anywhere by default.</summary>
    public static TranscriptionSettings Default { get; } = new(
        Enabled: false,
        BaseUrl: "https://api.openai.com/v1",
        Model: "whisper-1",
        SecretName: "openai-api-key",
        Timeout: TimeSpan.FromMinutes(2),
        Parameters: TranscriptionParameters.Default);
}

public interface ITranscriptionSettingsProvider
{
    Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs one transcription to completion: claim the entry, call the model, write the text back, recognise topics
/// and queue the embedding (docs/开发指导.md §8.2 step 5).
/// <para>
/// Extracted from the durable job handler so the upload path can do the work <em>inline</em> and hand the
/// transcript straight back to the phone, while the queue keeps doing exactly the same thing later when the
/// inline attempt did not happen or did not finish. One implementation, two callers: a second copy of this
/// sequence for the synchronous path would drift from the retried path, and the two would disagree about what
/// "transcribed" means — which is precisely what the client shows the user.
/// </para>
/// <para>
/// Implementations keep the job semantics intact: they throw the classified failures below, and they leave the
/// entry in a state a retry can pick up.
/// </para>
/// </summary>
public interface ITranscriptionRunner
{
    /// <summary>
    /// Transcribes one entry and returns the refreshed entry, or <c>null</c> when there was nothing to do
    /// (unknown, deleted, not a voice entry, or already transcribed).
    /// </summary>
    /// <param name="jobPayloadJson">The durable job's payload, when this is running as a job. Unused today;
    /// kept so the signature does not have to change when it is.</param>
    Task<Domain.Inputs.InputEntry?> RunAsync(
        Domain.Common.InputEntryId inputId,
        string? jobPayloadJson,
        CancellationToken cancellationToken);
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
