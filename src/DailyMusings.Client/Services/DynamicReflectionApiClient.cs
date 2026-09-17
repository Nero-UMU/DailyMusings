using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

/// <summary>
/// Builds the reflection client per call from the address currently in settings, the same way the capture client
/// does — so correcting the server address in 设置 takes effect on the next tap instead of after a restart.
/// </summary>
public sealed class DynamicReflectionApiClient : IReflectionApiClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public DynamicReflectionApiClient(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public Task<ApiResult<ReflectionDto>> GetAsync(string contentDate, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GetAsync(contentDate, cancellationToken));

    public Task<ApiResult<IReadOnlyList<ReflectionDto>>> ListAsync(
        string fromInclusive,
        string toInclusive,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListAsync(fromInclusive, toInclusive, cancellationToken));

    public Task<ApiResult<ReflectionDto>> EditAsync(
        string contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.EditAsync(contentDate, title, summary, body, cancellationToken));

    public Task<ApiResult<ReflectionDto>> SwitchVersionAsync(
        string contentDate,
        string versionId,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.SwitchVersionAsync(contentDate, versionId, cancellationToken));

    public Task<ApiResult<ReflectionDto>> ConfirmAsync(
        string contentDate,
        bool acceptedUnsourcedClaims,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ConfirmAsync(contentDate, acceptedUnsourcedClaims, cancellationToken));

    public Task<ApiResult<ReflectionGenerationResponse>> GenerateAsync(
        string contentDate,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GenerateAsync(
            contentDate,
            ignoreTranscriptionFailures,
            allowOverwriteOfManualEdits,
            cancellationToken));

    public Task<ApiResult<IReadOnlyList<PublishTargetDto>>> ListTargetsAsync(CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListTargetsAsync(cancellationToken));

    public Task<ApiResult<PublishResponse>> PublishAsync(
        string contentDate,
        string targetId,
        string visibility,
        bool replaceExistingFile,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.PublishAsync(contentDate, targetId, visibility, replaceExistingFile, cancellationToken));

    public Task<ApiResult<IReadOnlyList<PublicationDto>>> ListPublicationsAsync(
        string contentDate,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListPublicationsAsync(contentDate, cancellationToken));

    public Task<ApiResult<RemoteCheckResponse>> CheckRemoteAsync(
        string publicationId,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.CheckRemoteAsync(publicationId, cancellationToken));

    private async Task<ApiResult<T>> WithClientAsync<T>(Func<IReflectionApiClient, Task<ApiResult<T>>> work)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return ApiResult<T>.Unreachable("client.not_configured");
        }

        if (!await _tokens.HasTokenAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return ApiResult<T>.Unreachable("client.not_paired");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = Timeout };

        return await work(new HttpReflectionApiClient(http, _tokens)).ConfigureAwait(false);
    }
}
