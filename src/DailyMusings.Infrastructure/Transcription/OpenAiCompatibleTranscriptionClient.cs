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

/// <summary>Reads the four speech-model settings used by the transcription module.</summary>
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
            ApiType: StoredSettings.String(
                stored,
                DailyMusings.Application.Configuration.ModelSettingKeys.TranscriptionApiType,
                section.GetValue("ApiType", defaults.ApiType)) ?? defaults.ApiType);
    }
}

/// <summary>
/// Deep transcription module. Callers submit one recording and receive text; explicit <c>api_type</c> dispatch,
/// provider request shapes, asynchronous task polling and response parsing remain inside this implementation.
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

        var apiType = settings.ApiType.Trim().ToLowerInvariant();
        if (!TranscriptionApiTypes.IsSupported(apiType))
        {
            throw new PermanentExternalFailureException(
                "transcription.api_type_invalid",
                "The transcription api_type is not supported.");
        }

        var apiKey = _secrets.TryGet(settings.SecretName);
        if (apiKey is null)
        {
            throw new PermanentExternalFailureException(
                "transcription.secret_missing",
                $"The secret '{settings.SecretName}' is not provisioned.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        try
        {
            TranscriptionHttpAdapter.TranscriptionPayload? payload;
            if (apiType == TranscriptionApiTypes.DashScopeAsync)
            {
                payload = await TranscriptionHttpAdapter
                    .TranscribeDashScopeAsync(_httpClient, request, settings, apiKey, timeout.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                using var httpRequest = await TranscriptionHttpAdapter
                    .BuildRequestAsync(request, settings, apiKey, timeout.Token)
                    .ConfigureAwait(false);
                using var response = await _httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                EnsureSuccessfulResponse(response);
                payload = await TranscriptionHttpAdapter
                    .ReadPayloadAsync(response, apiType, timeout.Token)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(payload?.Text))
            {
                throw new PermanentExternalFailureException(
                    "transcription.empty_result",
                    "The transcription endpoint returned no text.");
            }

            return new TranscriptionResult(payload.Text, payload.Language, settings.Model);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
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
    }

    private void EnsureSuccessfulResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Transcription endpoint answered {StatusCode}.", (int)response.StatusCode);
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
    }
}

/// <summary>
/// Internal protocol adapter shared by real transcription and connection testing. It dispatches only on the
/// configured <c>api_type</c>; neither URL contents nor model names influence protocol selection.
/// </summary>
internal static class TranscriptionHttpAdapter
{
    private const string TranscriptionRelativePath = "audio/transcriptions";
    private const string ChatRelativePath = "chat/completions";
    private const string DashScopeSubmitRelativePath = "services/audio/asr/transcription";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<HttpRequestMessage> BuildRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        return settings.ApiType.Trim().ToLowerInvariant() switch
        {
            TranscriptionApiTypes.OpenAiTranscription =>
                await BuildOpenAiTranscriptionRequestAsync(request, settings, apiKey, cancellationToken)
                    .ConfigureAwait(false),
            TranscriptionApiTypes.OpenAiChatAudio =>
                await BuildOpenAiChatAudioRequestAsync(request, settings, apiKey, cancellationToken)
                    .ConfigureAwait(false),
            TranscriptionApiTypes.DashScopeAsync => throw PublicAudioUrlRequired(),
            _ => throw new PermanentExternalFailureException(
                "transcription.api_type_invalid",
                "The transcription api_type is not supported."),
        };
    }

    private static async Task<HttpRequestMessage> BuildOpenAiTranscriptionRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var content = new MultipartFormDataContent();
        var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);
        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        content.Add(fileContent, "file", request.FileName);
        content.Add(new StringContent(settings.Model), "model");

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(settings.BaseUrl, TranscriptionRelativePath))
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    private static async Task<HttpRequestMessage> BuildOpenAiChatAudioRequestAsync(
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        await using var audio = await request.OpenAudio(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Transcribe this audio and return only the transcript." },
                        new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                data = Convert.ToBase64String(buffer.ToArray()),
                                format = AudioFormat(request),
                            },
                        },
                    },
                },
            },
        });

        var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl, ChatRelativePath))
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    public static async Task<TranscriptionPayload?> ReadPayloadAsync(
        HttpResponseMessage response,
        string apiType,
        CancellationToken cancellationToken)
    {
        try
        {
            if (apiType == TranscriptionApiTypes.OpenAiChatAudio)
            {
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return new TranscriptionPayload(ExtractChatText(document.RootElement), null);
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

    public static async Task<TranscriptionPayload?> TranscribeDashScopeAsync(
        HttpClient httpClient,
        TranscriptionRequest request,
        TranscriptionSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (request.PublicAudioUrl is not { IsAbsoluteUri: true } publicUrl ||
            publicUrl.Scheme is not ("http" or "https"))
        {
            throw PublicAudioUrlRequired();
        }

        using var submitRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(settings.BaseUrl, DashScopeSubmitRelativePath))
        {
            Content = JsonContent.Create(new
            {
                model = settings.Model,
                input = new { file_urls = new[] { publicUrl.ToString() } },
            }),
        };
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        submitRequest.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");

        var submitted = await SendDashScopeRequestAsync(httpClient, submitRequest, cancellationToken)
            .ConfigureAwait(false);
        var taskId = submitted.Output?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw MalformedDashScopeResponse();
        }

        while (true)
        {
            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                BuildEndpoint(settings.BaseUrl, $"tasks/{Uri.EscapeDataString(taskId)}"));
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var status = await SendDashScopeRequestAsync(httpClient, statusRequest, cancellationToken)
                .ConfigureAwait(false);
            var taskStatus = status.Output?.TaskStatus?.Trim().ToUpperInvariant();

            if (taskStatus == "SUCCEEDED")
            {
                var text = await ReadDashScopeResultAsync(httpClient, status.Output!, cancellationToken)
                    .ConfigureAwait(false);
                return new TranscriptionPayload(text, null);
            }

            if (taskStatus is "FAILED" or "CANCELED" or "UNKNOWN")
            {
                throw new PermanentExternalFailureException(
                    "transcription.task_failed",
                    "The asynchronous transcription task failed.");
            }

            if (taskStatus is not ("PENDING" or "RUNNING"))
            {
                throw MalformedDashScopeResponse();
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<DashScopeTaskResponse> SendDashScopeRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        EnsureDashScopeSuccess(response);

        try
        {
            return await response.Content
                .ReadFromJsonAsync<DashScopeTaskResponse>(cancellationToken)
                .ConfigureAwait(false) ?? throw MalformedDashScopeResponse();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw MalformedDashScopeResponse();
        }
    }

    private static async Task<string?> ReadDashScopeResultAsync(
        HttpClient httpClient,
        DashScopeTaskOutput output,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(output.Text))
        {
            return output.Text;
        }

        var parts = new List<string>();
        foreach (var result in output.Results ?? [])
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                parts.Add(result.Text);
                continue;
            }

            if (!Uri.TryCreate(result.TranscriptionUrl, UriKind.Absolute, out var resultUrl) ||
                resultUrl.Scheme is not ("http" or "https"))
            {
                continue;
            }

            using var resultResponse = await httpClient
                .GetAsync(resultUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            EnsureDashScopeSuccess(resultResponse);

            DashScopeTranscriptDocument? document;
            try
            {
                document = await resultResponse.Content
                    .ReadFromJsonAsync<DashScopeTranscriptDocument>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw MalformedDashScopeResponse();
            }

            foreach (var transcript in document?.Transcripts ?? [])
            {
                if (!string.IsNullOrWhiteSpace(transcript.Text))
                {
                    parts.Add(transcript.Text);
                }
                else
                {
                    parts.AddRange((transcript.Sentences ?? [])
                        .Select(sentence => sentence.Text)
                        .Where(text => !string.IsNullOrWhiteSpace(text))!);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private static string? ExtractChatText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message))
        {
            return null;
        }

        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text));
                return string.Join('\n', parts!);
            }
        }

        return message.TryGetProperty("audio", out var audio) &&
               audio.TryGetProperty("transcript", out var transcript) &&
               transcript.ValueKind == JsonValueKind.String
            ? transcript.GetString()
            : null;
    }

    private static void EnsureDashScopeSuccess(HttpResponseMessage response)
    {
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
    }

    /// <summary>Joins the user-supplied base URL and relative path without duplicate slashes.</summary>
    private static Uri BuildEndpoint(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new Uri($"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}", UriKind.Absolute);
    }

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

    private static PermanentExternalFailureException PublicAudioUrlRequired() => new(
        "transcription.public_audio_url_required",
        "dashscope_async requires a publicly reachable HTTP(S) audio URL; the current recording is local only.");

    private static PermanentExternalFailureException MalformedDashScopeResponse() => new(
        "transcription.malformed_response",
        "The transcription endpoint returned a response that could not be read.");

    internal sealed record TranscriptionPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);

    private sealed record DashScopeTaskResponse(
        [property: JsonPropertyName("output")] DashScopeTaskOutput? Output);

    private sealed record DashScopeTaskOutput(
        [property: JsonPropertyName("task_id")] string? TaskId,
        [property: JsonPropertyName("task_status")] string? TaskStatus,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("results")] IReadOnlyList<DashScopeTaskResult>? Results);

    private sealed record DashScopeTaskResult(
        [property: JsonPropertyName("transcription_url")] string? TranscriptionUrl,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record DashScopeTranscriptDocument(
        [property: JsonPropertyName("transcripts")] IReadOnlyList<DashScopeTranscript>? Transcripts);

    private sealed record DashScopeTranscript(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("sentences")] IReadOnlyList<DashScopeSentence>? Sentences);

    private sealed record DashScopeSentence(
        [property: JsonPropertyName("text")] string? Text);
}
