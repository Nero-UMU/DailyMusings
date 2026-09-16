using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Transcription;

/// <summary>
/// Reads the transcription endpoint's configuration from the deployment configuration
/// (docs/开发指导.md §8.1). Disabled unless an operator turns it on, so a fresh instance never sends audio
/// anywhere by accident.
/// <para>
/// The admin-page editor for these values belongs to the phase that owns model configuration; until then the
/// environment is the configuration surface, and the shape is already the one the editor will write.
/// </para>
/// </summary>
public sealed class ConfigurationTranscriptionSettingsProvider : ITranscriptionSettingsProvider
{
    public const string SectionName = "Transcription";

    private readonly TranscriptionSettings _settings;

    public ConfigurationTranscriptionSettingsProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        _settings = new TranscriptionSettings(
            Enabled: section.GetValue("Enabled", TranscriptionSettings.Default.Enabled),
            BaseUrl: section.GetValue("BaseUrl", TranscriptionSettings.Default.BaseUrl) ?? TranscriptionSettings.Default.BaseUrl,
            Model: section.GetValue("Model", TranscriptionSettings.Default.Model) ?? TranscriptionSettings.Default.Model,
            SecretName: section.GetValue("SecretName", TranscriptionSettings.Default.SecretName) ?? TranscriptionSettings.Default.SecretName,
            Timeout: TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 120)),
            LanguageHint: section.GetValue<string?>("LanguageHint"));
    }

    public Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_settings);
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
    private const string RelativePath = "audio/transcriptions";

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

        using var httpRequest = await BuildRequestAsync(request, settings, apiKey, cancellationToken)
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

            var payload = await ReadPayloadAsync(response, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
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

        var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl))
        {
            Content = content,
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>Joins the base URL and the relative path without dropping or doubling a slash.</summary>
    private static Uri BuildEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return new Uri($"{baseUrl.TrimEnd('/')}/{RelativePath}", UriKind.Absolute);
    }

    private sealed record TranscriptionPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);
}
