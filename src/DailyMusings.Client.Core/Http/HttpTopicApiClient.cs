using DailyMusings.Client.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Client.Core.Topics;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Http;

/// <summary>
/// The topic vocabulary over HTTP (docs/开发指导.md §6.2, §13).
/// <para>
/// Same discipline as the other clients: classified answers, no exception for a failure the user can act on, and the
/// server's own refusal codes carried through untouched.
/// </para>
/// </summary>
public sealed class HttpTopicApiClient : ITopicApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IDeviceTokenProvider _tokenProvider;

    public HttpTopicApiClient(HttpClient httpClient, IDeviceTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    public Task<ApiResult<IReadOnlyList<TopicDto>>> ListAsync(bool includeMerged, CancellationToken cancellationToken) =>
        SendListAsync<TopicListResponse, TopicDto>(
            HttpMethod.Get,
            $"/api/topics?includeMerged={(includeMerged ? "true" : "false")}",
            null,
            response => response.Items,
            cancellationToken);

    public Task<ApiResult<TopicDto>> CreateAsync(string name, CancellationToken cancellationToken) =>
        SendAsync<TopicDto>(HttpMethod.Post, "/api/topics", new CreateTopicRequest(name), cancellationToken);

    public Task<ApiResult<TopicDto>> RenameAsync(string topicId, string name, CancellationToken cancellationToken) =>
        SendAsync<TopicDto>(HttpMethod.Patch, $"/api/topics/{topicId}", new RenameTopicRequest(name), cancellationToken);

    public Task<ApiResult<TopicMergeResponse>> MergeAsync(
        string sourceTopicId,
        string targetTopicId,
        CancellationToken cancellationToken) =>
        SendAsync<TopicMergeResponse>(
            HttpMethod.Post,
            "/api/topics/merge",
            new MergeTopicsRequest(sourceTopicId, targetTopicId),
            cancellationToken);

    public Task<ApiResult<InputTopicAssignmentResponse>> AssignAsync(
        string inputId,
        string? primaryTopicId,
        IReadOnlyList<string> secondaryTopicIds,
        CancellationToken cancellationToken) =>
        SendAsync<InputTopicAssignmentResponse>(
            HttpMethod.Put,
            $"/api/inputs/{inputId}/topics",
            new AssignInputTopicsRequest(primaryTopicId, secondaryTopicIds),
            cancellationToken);

    private async Task<ApiResult<IReadOnlyList<TItem>>> SendListAsync<TResponse, TItem>(
        HttpMethod method,
        string path,
        object? body,
        Func<TResponse, IReadOnlyList<TItem>> select,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var result = await SendAsync<TResponse>(method, path, body, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return ApiResult<IReadOnlyList<TItem>>.From(select(result.Value!));
        }

        return result.ServerReached
            ? ApiResult<IReadOnlyList<TItem>>.Refused(result.FailureCode ?? "client.empty_response")
            : ApiResult<IReadOnlyList<TItem>>.Unreachable(result.FailureCode ?? "client.request_failed");
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
        where T : class
    {
        string? token;

        try
        {
            token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApiResult<T>.Unreachable("client.token_unavailable");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ApiResult<T>.Unreachable("client.not_paired");
        }

        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Unreachable("client.network_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Unreachable("client.timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApiResult<T>.Unreachable("client.request_failed");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ApiResult<T>.Refused("auth.device_token_rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<T>.Refused(await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? $"server.rejected.{(int)response.StatusCode}");
            }

            try
            {
                var value = await response.Content
                    .ReadFromJsonAsync<T>(cancellationToken)
                    .ConfigureAwait(false);

                return value is null ? ApiResult<T>.Refused("client.empty_response") : ApiResult<T>.From(value);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return ApiResult<T>.Refused("client.malformed_response");
            }
        }
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
