using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Settings;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Http;

/// <summary>
/// The reads the capture screen makes about things it did not capture: an entry's stored audio, and the instance's
/// notification preferences (docs/开发指导.md §9.1, §9.3, §12, §15.2 step 6).
/// <para>
/// Kept beside <see cref="HttpCaptureApiClient"/> rather than inside it: these are the two places where the client
/// reads something other than its own uploads, and both use the same classified-answer discipline.
/// </para>
/// </summary>
public sealed class HttpInstanceApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IDeviceTokenProvider _tokenProvider;

    public HttpInstanceApiClient(HttpClient httpClient, IDeviceTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Fetches one entry's stored recording. A.1 keeps audio for thirty days precisely so a transcript can be checked
    /// against it, which needs a read path; the server answers 404 with <c>input.audio.deleted</c> once retention has
    /// removed it.
    /// </summary>
    public async Task<ApiResult<AudioClip>> GetAudioAsync(string inputId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputId);

        using var request = await AuthorizeAsync(HttpMethod.Get, $"/api/inputs/{inputId}/audio", cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return ApiResult<AudioClip>.Unreachable("client.not_paired");
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult<AudioClip>.Unreachable("client.network_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<AudioClip>.Unreachable("client.timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApiResult<AudioClip>.Unreachable("client.request_failed");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ApiResult<AudioClip>.Refused("auth.device_token_rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<AudioClip>.Refused(await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? $"server.rejected.{(int)response.StatusCode}");
            }

            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                return bytes.Length == 0
                    ? ApiResult<AudioClip>.Refused("client.empty_response")
                    : ApiResult<AudioClip>.From(new AudioClip(
                        bytes,
                        response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"));
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException)
            {
                return ApiResult<AudioClip>.Refused("client.malformed_response");
            }
        }
    }

    public async Task<ApiResult<NotificationSettingsDto>> GetNotificationSettingsAsync(CancellationToken cancellationToken)
    {
        using var request = await AuthorizeAsync(HttpMethod.Get, "/api/notification-settings", cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return ApiResult<NotificationSettingsDto>.Unreachable("client.not_paired");
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult<NotificationSettingsDto>.Unreachable("client.network_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<NotificationSettingsDto>.Unreachable("client.timeout");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The server keeps this administrator-only for now, and a device asking is told so rather than being
                // shown an empty form that cannot be saved.
                return ApiResult<NotificationSettingsDto>.Refused("auth.forbidden");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<NotificationSettingsDto>.Refused($"server.rejected.{(int)response.StatusCode}");
            }

            try
            {
                var settings = await response.Content
                    .ReadFromJsonAsync<NotificationSettingsDto>(cancellationToken)
                    .ConfigureAwait(false);

                return settings is null
                    ? ApiResult<NotificationSettingsDto>.Refused("client.empty_response")
                    : ApiResult<NotificationSettingsDto>.From(settings);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return ApiResult<NotificationSettingsDto>.Refused("client.malformed_response");
            }
        }
    }

    /// <summary>Reads the configured model names, for the settings screen.</summary>
    public async Task<ApiResult<IReadOnlyList<ModelNameDto>>> GetModelNamesAsync(CancellationToken cancellationToken)
    {
        using var request = await AuthorizeAsync(HttpMethod.Get, "/api/system/models", cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.not_paired");
        }

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ApiResult<IReadOnlyList<ModelNameDto>>.Refused("auth.device_token_rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<IReadOnlyList<ModelNameDto>>.Refused($"server.rejected.{(int)response.StatusCode}");
            }

            var page = await response.Content
                .ReadFromJsonAsync<ModelNameListResponse>(cancellationToken)
                .ConfigureAwait(false);

            return ApiResult<IReadOnlyList<ModelNameDto>>.From(page?.Items ?? []);
        }
        catch (HttpRequestException)
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.network_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.timeout");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Refused("client.malformed_response");
        }
    }

    private async Task<HttpRequestMessage?> AuthorizeAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        string? token;

        try
        {
            token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ApiError>(cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(error?.Code) ? null : error.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
