using DailyMusings.Client.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Contracts;
using DailyMusings.Client.Core.Reflections;

namespace DailyMusings.Client.Core.Http;

/// <summary>
/// The draft screen's half of the API (docs/开发指导.md §6.3, §8.4, §11.1).
/// <para>
/// Every call leaves here as a classified <see cref="ReflectionResult{T}"/>. Two of these paths are refusals the
/// user is expected to act on rather than errors — confirming a draft with untraced sentences, regenerating a day
/// with hand edits — and the server names them with stable codes, so the code is carried through untouched and the
/// screen decides what to say. Transport failures never throw: this code runs while a page appears.
/// </para>
/// </summary>
public sealed class HttpReflectionApiClient : IReflectionApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IDeviceTokenProvider _tokenProvider;

    public HttpReflectionApiClient(HttpClient httpClient, IDeviceTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    public Task<ApiResult<ReflectionDto>> GetAsync(string contentDate, CancellationToken cancellationToken) =>
        SendAsync<ReflectionDto>(HttpMethod.Get, $"/api/reflections/{contentDate}", null, cancellationToken);

    public Task<ApiResult<IReadOnlyList<ReflectionDto>>> ListAsync(
        string fromInclusive,
        string toInclusive,
        CancellationToken cancellationToken) =>
        SendListAsync<ReflectionListResponse, ReflectionDto>(
            HttpMethod.Get,
            $"/api/reflections?from={Uri.EscapeDataString(fromInclusive)}&to={Uri.EscapeDataString(toInclusive)}",
            null,
            response => response.Items,
            cancellationToken);

    public Task<ApiResult<ReflectionDto>> EditAsync(
        string contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken) =>
        SendAsync<ReflectionDto>(
            HttpMethod.Patch,
            $"/api/reflections/{contentDate}/working-version/content",
            new EditReflectionRequest(title, summary, body),
            cancellationToken);

    public Task<ApiResult<ReflectionDto>> SwitchVersionAsync(
        string contentDate,
        string versionId,
        CancellationToken cancellationToken) =>
        SendAsync<ReflectionDto>(
            HttpMethod.Patch,
            $"/api/reflections/{contentDate}/working-version",
            new SwitchWorkingVersionRequest(versionId),
            cancellationToken);

    public Task<ApiResult<ReflectionDto>> ConfirmAsync(
        string contentDate,
        bool acceptedUnsourcedClaims,
        CancellationToken cancellationToken) =>
        SendAsync<ReflectionDto>(
            HttpMethod.Post,
            $"/api/reflections/{contentDate}/confirm",
            new ConfirmReflectionRequest(acceptedUnsourcedClaims),
            cancellationToken);

    public Task<ApiResult<ReflectionGenerationResponse>> GenerateAsync(
        string contentDate,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken) =>
        SendAsync<ReflectionGenerationResponse>(
            HttpMethod.Post,
            $"/api/reflections/{contentDate}/generate",
            new GenerateReflectionRequest(ignoreTranscriptionFailures, allowOverwriteOfManualEdits),
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<PublishTargetDto>>> ListTargetsAsync(CancellationToken cancellationToken) =>
        SendListAsync<PublishTargetListResponse, PublishTargetDto>(
            HttpMethod.Get,
            "/api/publish-targets",
            null,
            response => response.Items,
            cancellationToken);

    public Task<ApiResult<PublishResponse>> PublishAsync(
        string contentDate,
        string targetId,
        string visibility,
        bool replaceExistingFile,
        CancellationToken cancellationToken) =>
        SendAsync<PublishResponse>(
            HttpMethod.Post,
            $"/api/reflections/{contentDate}/publish/{targetId}",
            new PublishRequest(visibility, replaceExistingFile),
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<PublicationDto>>> ListPublicationsAsync(
        string contentDate,
        CancellationToken cancellationToken) =>
        SendListAsync<PublicationListResponse, PublicationDto>(
            HttpMethod.Get,
            $"/api/reflections/{contentDate}/publications",
            null,
            response => response.Items,
            cancellationToken);

    public Task<ApiResult<RemoteCheckResponse>> CheckRemoteAsync(
        string publicationId,
        CancellationToken cancellationToken) =>
        SendAsync<RemoteCheckResponse>(
            HttpMethod.Post,
            $"/api/publications/{publicationId}/check-remote",
            new { },
            cancellationToken);

    /// <summary>
    /// A list response, unwrapped into the items the screen actually wants. Kept next to
    /// <see cref="SendAsync{T}"/> so the classification is written once.
    /// </summary>
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

    /// <summary>
    /// One request, classified. Nothing here throws for a failure the user could act on, and nothing logs the draft:
    /// the body of a day's reflection is the user's private writing (§16).
    /// </summary>
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
            // Anything else the HTTP stack surfaces is still "the draft is not available right now", never a reason to
            // take the screen down — the same lesson the capture loop learned from a blackholed network.
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
                // The server's own code, verbatim: several of these are instructions to the user rather than errors
                // (reflection.confirm.unsourced_claims_not_acknowledged, reflection.regeneration.*,
                // publication.*), and inventing a client-side code for them would lose that.
                return ApiResult<T>.Refused(await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false)
                    ?? $"server.rejected.{(int)response.StatusCode}");
            }

            try
            {
                var value = await response.Content
                    .ReadFromJsonAsync<T>(cancellationToken)
                    .ConfigureAwait(false);

                return value is null
                    ? ApiResult<T>.Refused("client.empty_response")
                    : ApiResult<T>.From(value);
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
