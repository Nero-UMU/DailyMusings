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
        var defaultParameters = defaults.Parameters;
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
            Parameters: new TranscriptionParameters(
                Protocol: StoredSettings.String(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionProtocol,
                    section.GetValue("Protocol", defaultParameters.Protocol)) ?? defaultParameters.Protocol,
                LanguageHints: StoredSettings.Username(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionLanguageHints,
                    section.GetValue<string?>("LanguageHints") ?? section.GetValue<string?>("LanguageHint")),
                EnableItn: StoredSettings.Boolean(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionEnableItn,
                    section.GetValue("EnableItn", defaultParameters.EnableItn)),
                VocabularyId: StoredSettings.Username(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionVocabularyId,
                    section.GetValue<string?>("VocabularyId")),
                SpeakerDiarization: StoredSettings.Boolean(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionSpeakerDiarization,
                    section.GetValue("SpeakerDiarization", defaultParameters.SpeakerDiarization)),
                KeepDialect: StoredSettings.Boolean(
                    stored,
                    DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionKeepDialect,
                    section.GetValue("KeepDialect", defaultParameters.KeepDialect))));
    }
}

/// <summary>
/// Speech-to-text through the configured protocol adapter (docs/开发指导.md §8.1, §8.2).
/// <para>
/// Two rules govern this class. It never logs the audio or the resulting text — §16 keeps content out of the
/// default log, and a transcript is the most private thing the product holds. And it classifies every failure as
/// transient or permanent, because that classification is what decides whether §14 retries or gives up.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleTranscriptionClient : ITranscriptionClient
{
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

        if (TranscriptionProtocolNames.RequiresUnsupportedTransport(settings.Model))
        {
            throw new PermanentExternalFailureException(
                "transcription.protocol_unsupported",
                "This transcription model requires a realtime stream or a publicly reachable file URL.");
        }

        var protocol = TranscriptionProtocolNames.Resolve(settings.Parameters.Protocol, settings.Model);

        using var httpRequest = await TranscriptionHttpAdapter.BuildRequestAsync(request, settings, apiKey, cancellationToken)
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

            var payload = await TranscriptionHttpAdapter
                .ReadPayloadAsync(response, protocol, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload?.Text))
            {
                throw new PermanentExternalFailureException(
                    "transcription.empty_result",
                    "The transcription endpoint returned no text.");
            }

            return new TranscriptionResult(payload.Text, payload.Language, settings.Model);
        }
    }

}

/// <summary>
/// The deep protocol module behind both production transcription and the admin connection probe. Callers provide
/// one ordinary recording and settings; endpoint selection, provider payloads and response shapes stay local here.
/// </summary>
internal static class TranscriptionHttpAdapter
{
    private const string WhisperRelativePath = "audio/transcriptions";
    private const string ChatCompletionsRelativePath = "chat/completions";
    private const string DashScopeRelativePath = "api/v1/services/aigc/multimodal-generation/generation";

    public static async Task<HttpRequestMessage> BuildRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var protocol = TranscriptionProtocolNames.Resolve(settings.Parameters.Protocol, settings.Model);
        if (protocol == TranscriptionProtocolNames.QwenAsrChat)
        {
            return await BuildQwenAsrRequestAsync(request, settings, apiKey, cancellationToken)
                .ConfigureAwait(false);
        }

        if (protocol == TranscriptionProtocolNames.DashScopeMultimodal)
        {
            return await BuildDashScopeRequestAsync(request, settings, apiKey, cancellationToken)
                .ConfigureAwait(false);
        }

        var content = new MultipartFormDataContent();
        var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);

        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        content.Add(fileContent, "file", request.FileName);
        content.Add(new StringContent(settings.Model), "model");

        var language = request.LanguageHint ?? Languages(settings.Parameters.LanguageHints).FirstOrDefault();
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

    private static async Task<HttpRequestMessage> BuildQwenAsrRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        await using var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var dataUri = $"data:{request.ContentType};base64,{Convert.ToBase64String(buffer.ToArray())}";
        var options = new Dictionary<string, object>
        {
            ["enable_itn"] = settings.Parameters.EnableItn,
        };
        if ((request.LanguageHint ?? Languages(settings.Parameters.LanguageHints).FirstOrDefault()) is { } language)
        {
            options["language"] = language;
        }

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
            asr_options = options,
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

    private static async Task<HttpRequestMessage> BuildDashScopeRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var dataUri = await DataUriAsync(request, cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, object>
        {
            ["format"] = AudioFormat(request),
        };

        // Some DashScope models reject parameters they do not implement, even when the value is false.
        // Only send optional switches the user actually enabled.
        if (settings.Parameters.SpeakerDiarization)
        {
            parameters["speaker_diarization_enabled"] = true;
        }

        if (settings.Parameters.KeepDialect)
        {
            parameters["keep_dialect"] = true;
        }

        var languages = Languages(settings.Parameters.LanguageHints);
        if (request.LanguageHint is { Length: > 0 } requestLanguage)
        {
            languages = [requestLanguage.Trim().ToLowerInvariant()];
        }

        if (languages.Length > 0)
        {
            parameters["language_hints"] = languages;
        }

        if (!string.IsNullOrWhiteSpace(settings.Parameters.VocabularyId))
        {
            parameters["vocabulary_id"] = settings.Parameters.VocabularyId.Trim();
        }

        var content = JsonContent.Create(new
        {
            model = settings.Model,
            input = new
            {
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
            },
            parameters,
        });

        var message = new HttpRequestMessage(HttpMethod.Post, BuildDashScopeEndpoint(settings.BaseUrl))
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.TryAddWithoutValidation("X-DashScope-SSE", "disable");
        return message;
    }

    public static async Task<TranscriptionPayload?> ReadPayloadAsync(
        HttpResponseMessage response,
        string protocol,
        CancellationToken cancellationToken)
    {
        try
        {
            if (protocol == TranscriptionProtocolNames.QwenAsrChat)
            {
                var chat = await response.Content
                    .ReadFromJsonAsync<AudioMessageResponse>(cancellationToken)
                    .ConfigureAwait(false);
                return new TranscriptionPayload(chat?.Choices?.FirstOrDefault()?.Message?.Content, null);
            }

            if (protocol == TranscriptionProtocolNames.DashScopeMultimodal)
            {
                var dashScope = await response.Content
                    .ReadFromJsonAsync<DashScopeResponse>(cancellationToken)
                    .ConfigureAwait(false);
                var text = dashScope?.Output?.Text;
                if (string.IsNullOrWhiteSpace(text) && dashScope?.Output?.Sentences is { Count: > 0 } sentences)
                {
                    text = string.Join('\n', sentences
                        .Select(sentence => sentence.Text)
                        .Where(sentence => !string.IsNullOrWhiteSpace(sentence)));
                }

                return new TranscriptionPayload(text, null);
            }

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

    /// <summary>Joins the base URL and the relative path without dropping or doubling a slash.</summary>
    private static Uri BuildEndpoint(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return new Uri($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }

    private static async Task<string> DataUriAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        await using var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return $"data:{request.ContentType};base64,{Convert.ToBase64String(buffer.ToArray())}";
    }

    private static string[] Languages(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string AudioFormat(TranscriptionRequest request)
    {
        var extension = Path.GetExtension(request.FileName).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        return request.ContentType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "m4a",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            _ => "wav",
        };
    }

    private static Uri BuildDashScopeEndpoint(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        const string compatibleSuffix = "/compatible-mode/v1";
        if (root.EndsWith(compatibleSuffix, StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^compatibleSuffix.Length];
        }
        else if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/api/v1".Length];
        }

        return BuildEndpoint(root, DashScopeRelativePath);
    }

    internal sealed record TranscriptionPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);

    private sealed record AudioMessageResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<AudioMessageChoice>? Choices);

    private sealed record AudioMessageChoice(
        [property: JsonPropertyName("message")] AudioMessage? Message);

    private sealed record AudioMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record DashScopeResponse(
        [property: JsonPropertyName("output")] DashScopeOutput? Output);

    private sealed record DashScopeOutput(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("sentences")] IReadOnlyList<DashScopeSentence>? Sentences);

    private sealed record DashScopeSentence(
        [property: JsonPropertyName("text")] string? Text);
}
