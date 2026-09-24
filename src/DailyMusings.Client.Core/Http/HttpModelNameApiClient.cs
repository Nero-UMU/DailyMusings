using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Http;

/// <summary>
/// Reads the instance's configured model names for the settings screen (docs/开发指导.md §8.1).
/// <para>
/// Replaces the instance client that also carried notification preferences and server-side audio: the phone no
/// longer shows either, so what is left is one read. It keeps the same discipline as the capture client — a missing
/// token and an unreachable server come back as classified results rather than exceptions, because this read runs
/// while the settings screen is appearing.
/// </para>
/// </summary>
public sealed class HttpModelNameApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IDeviceTokenProvider _tokenProvider;

    public HttpModelNameApiClient(HttpClient httpClient, IDeviceTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    public async Task<ApiResult<IReadOnlyList<ModelNameDto>>> GetModelNamesAsync(CancellationToken cancellationToken)
    {
        string? token;

        try
        {
            token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A secure-storage read that fails is "no token", not a crash on the settings screen.
            token = null;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.not_paired");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
}
