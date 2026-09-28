using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Transcription;

/// <summary>
/// Reads the transcription endpoint's configuration (docs/开发指导.md §8.1). Disabled unless an operator turns it on,
/// so a fresh instance never sends audio anywhere by accident.
/// <para>
/// The settings table comes first — the admin page writes there — and the deployment configuration is the fallback,
/// so an instance can be configured either way. The read happens per call so a saved change takes effect on the next
/// transcription instead of the next restart.
/// </para>
/// </summary>
public sealed class ConfigurationTranscriptionSettingsProvider : ITranscriptionSettingsProvider
{
    public const string SectionName = "Transcription";

    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public ConfigurationTranscriptionSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var section = _configuration.GetSection(SectionName);
        var defaults = TranscriptionSettings.Default;
        var service = DailyMusings.Application.Configuration.ModelService.Transcription;

        return new TranscriptionSettings(
            Enabled: StoredSettings.Boolean(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.Enabled(service),
                section.GetValue("Enabled", defaults.Enabled)),
            BaseUrl: StoredSettings.String(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.BaseUrl(service),
                section.GetValue("BaseUrl", defaults.BaseUrl)) ?? defaults.BaseUrl,
            Model: StoredSettings.String(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.Model(service),
                section.GetValue("Model", defaults.Model)) ?? defaults.Model,
            SecretName: StoredSettings.String(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.SecretName(service),
                section.GetValue("SecretName", defaults.SecretName)) ?? defaults.SecretName,
            Timeout: TimeSpan.FromSeconds(StoredSettings.Integer(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.TimeoutSeconds(service),
                section.GetValue("TimeoutSeconds", (int)defaults.Timeout.TotalSeconds))),
            LanguageHint: section.GetValue<string?>("LanguageHint"));
    }
}

/// <summary>
/// Speech-to-text against an OpenAI-compatible <c>/audio/transcriptions</c> endpoint
/// (docs/开发指导.md §8.1, §8.2).
/// <para>
/// Two rules govern this class. It never logs the audio or the resulting text — §16 keeps content out of the
/// default log, and a transcript is the most private thing the product holds. And it classifies every failure as
/// transient or permanent, because that classification is what decides whether §14 retries or gives up.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleTranscriptionClient : ITranscriptionClient
{
    private const string WhisperRelativePath = "audio/transcriptions";
    private const string ChatCompletionsRelativePath = "chat/completions";

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly ITranscriptionSettingsProvider _settings;
    private readonly ILogger<OpenAiCompatibleTranscriptionClient> _logger;

    public OpenAiCompatibleTranscriptionClient(
        HttpClient httpClient,
        ISecretStore secrets,
        ITranscriptionSettingsProvider settings,
        ILogger<OpenAiCompatibleTranscriptionClient> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            throw new PermanentExternalFailureException(
                "transcription.disabled",
                "No transcription endpoint is configured.");
        }

        var apiKey = _secrets.TryGet(settings.SecretName);
        if (apiKey is null)
        {
            throw new PermanentExternalFailureException(
                "transcription.secret_missing",
                $"The secret '{settings.SecretName}' is not provisioned.");
        }

        if (UsesPublicUrlOnlyProtocol(settings.Model))
        {
            throw new PermanentExternalFailureException(
                "transcription.model_requires_public_audio",
                "This transcription model only accepts a publicly reachable audio URL. Choose a model that accepts uploaded audio, such as qwen3-asr-flash or whisper-1.");
        }

        var usesAudioMessage = UsesAudioMessageProtocol(settings.Model);

        using var httpRequest = await BuildRequestAsync(request, settings, apiKey, usesAudioMessage, cancellationToken)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Distinguish "our deadline elapsed" from "the caller gave up": only the former is retryable.
            throw new TransientExternalFailureException(
                "transcription.timeout",
                "The transcription endpoint did not answer in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientExternalFailureException(
                "transcription.network",
                "The transcription endpoint could not be reached.",
                exception);
        }

        using (response)
        {
            // Only the status code is logged; never the audio, the payload or the transcript.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Transcription endpoint answered {StatusCode}.",
                    (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PermanentExternalFailureException(
                    "transcription.credentials_rejected",
                    "The transcription endpoint rejected the configured credentials.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new TransientExternalFailureException(
                    "transcription.upstream_unavailable",
                    "The transcription endpoint is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PermanentExternalFailureException(
                    "transcription.request_rejected",
                    "The transcription endpoint rejected the request.");
            }

            var payload = usesAudioMessage
                ? await ReadAudioMessagePayloadAsync(response, cancellationToken).ConfigureAwait(false)
                : await ReadPayloadAsync(response, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload?.Text))
            {
                throw new PermanentExternalFailureException(
                    "transcription.empty_result",
                    "The transcription endpoint returned no text.");
            }

            return new TranscriptionResult(payload.Text, payload.Language, settings.Model);
        }
    }

    /// <summary>
    /// Reads the endpoint's answer, turning a shape we cannot understand into a permanent failure.
    /// <para>
    /// Found by running the real pipeline: a model that answers with something unexpected (a field of the wrong
    /// type, an error document, a provider-specific wrapper) used to escape as an unclassified exception, which
    /// left the entry stuck in progress and the job reported as an internal defect. Retrying the same request would
    /// produce the same unreadable answer, so it belongs in the permanent bucket with a code the user can act on.
    /// </para>
    /// </summary>
    private static async Task<TranscriptionPayload?> ReadPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<TranscriptionPayload>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                "transcription.malformed_response",
                "The transcription endpoint returned a response that could not be read.");
        }
    }

    private static async Task<HttpRequestMessage> BuildRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        bool usesAudioMessage,
        CancellationToken cancellationToken)
    {
        if (usesAudioMessage)
        {
            return await BuildAudioMessageRequestAsync(request, settings, apiKey, cancellationToken)
                .ConfigureAwait(false);
        }

        var content = new MultipartFormDataContent();
        var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);

        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        content.Add(fileContent, "file", request.FileName);
        content.Add(new StringContent(settings.Model), "model");

        var language = request.LanguageHint ?? settings.LanguageHint;
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Add(new StringContent(language), "language");
        }

        var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl, WhisperRelativePath))
        {
            Content = content,
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    private static async Task<HttpRequestMessage> BuildAudioMessageRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        await using var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var dataUri = $"data:{request.ContentType};base64,{Convert.ToBase64String(buffer.ToArray())}";
        var content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = dataUri },
                        },
                    },
                },
            },
            stream = false,
        });

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(settings.BaseUrl, ChatCompletionsRelativePath))
        {
            Content = content,
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>Joins the base URL and the relative path without dropping or doubling a slash.</summary>
    private static Uri BuildEndpoint(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return new Uri($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }

    private static bool UsesAudioMessageProtocol(string model) =>
        model.StartsWith("qwen3-asr-flash", StringComparison.OrdinalIgnoreCase);

    private static bool UsesPublicUrlOnlyProtocol(string model) =>
        model.StartsWith("paraformer", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("filetrans", StringComparison.OrdinalIgnoreCase);

    private static async Task<TranscriptionPayload?> ReadAudioMessagePayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content
                .ReadFromJsonAsync<AudioMessageResponse>(cancellationToken)
                .ConfigureAwait(false);

            var text = payload?.Choices?.FirstOrDefault()?.Message?.Content;
            return new TranscriptionPayload(text, null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                "transcription.malformed_response",
                "The transcription endpoint returned a response that could not be read.");
        }
    }

    private sealed record TranscriptionPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);

    private sealed record AudioMessageResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<AudioMessageChoice>? Choices);

    private sealed record AudioMessageChoice(
        [property: JsonPropertyName("message")] AudioMessage? Message);

    private sealed record AudioMessage(
        [property: JsonPropertyName("content")] string? Content);
}
