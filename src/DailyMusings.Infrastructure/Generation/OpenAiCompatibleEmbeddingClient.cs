using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Generation;

/// <summary>
/// Embeddings against an OpenAI-compatible <c>/embeddings</c> endpoint (docs/开发指导.md §8.1 item 3, §8.3).
/// <para>
/// Optional by design: with it switched off, or with its index stale, retrieval still works from topics and full
/// text. Failure classification follows the same convention as the other endpoints, because the index rebuild is
/// a job that §14 retries.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleEmbeddingClient : IEmbeddingClient
{
    private const string RelativePath = "embeddings";

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly IEmbeddingSettingsProvider _settings;
    private readonly ILogger<OpenAiCompatibleEmbeddingClient> _logger;

    public OpenAiCompatibleEmbeddingClient(
        HttpClient httpClient,
        ISecretStore secrets,
        IEmbeddingSettingsProvider settings,
        ILogger<OpenAiCompatibleEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Inputs.Count == 0)
        {
            return [];
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            throw new PermanentExternalFailureException(
                "embedding.disabled",
                "No embedding endpoint is configured.");
        }

        var apiKey = _secrets.TryGet(settings.SecretName);
        if (apiKey is null)
        {
            throw new PermanentExternalFailureException(
                "embedding.secret_missing",
                $"The secret '{settings.SecretName}' is not provisioned.");
        }

        var body = new EmbeddingBody(
            request.Model,
            request.Inputs,
            request.Dimensions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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
            throw new TransientExternalFailureException(
                "embedding.timeout",
                "The embedding endpoint did not answer in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientExternalFailureException(
                "embedding.network",
                "The embedding endpoint could not be reached.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Only the status code is logged; never the embedded text (§16).
                _logger.LogWarning("Embedding endpoint answered {StatusCode}.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PermanentExternalFailureException(
                    "embedding.credentials_rejected",
                    "The embedding endpoint rejected the configured credentials.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new TransientExternalFailureException(
                    "embedding.upstream_unavailable",
                    "The embedding endpoint is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PermanentExternalFailureException(
                    "embedding.request_rejected",
                    "The embedding endpoint rejected the request.");
            }

            var payload = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var data = payload?.Data;

            if (data is null || data.Count == 0)
            {
                throw new PermanentExternalFailureException(
                    "embedding.empty_response",
                    "The embedding endpoint returned no vectors.");
            }

            return data
                .Where(item => item.Embedding is { Length: > 0 })
                .Select(item => new EmbeddingResult(item.Index, item.Embedding!))
                .ToArray();
        }
    }

    private static async Task<EmbeddingPayload?> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<EmbeddingPayload>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                "embedding.malformed_response",
                "The embedding endpoint returned a response that could not be read.");
        }
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new Uri($"{baseUrl.TrimEnd('/')}/{RelativePath}", UriKind.Absolute);
    }

    private sealed record EmbeddingBody(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("dimensions")] int? Dimensions);

    private sealed record EmbeddingPayload(
        [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
