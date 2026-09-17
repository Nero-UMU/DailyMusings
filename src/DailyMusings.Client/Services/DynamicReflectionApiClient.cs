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

    public Task<ReflectionResult<ReflectionDto>> GetAsync(string contentDate, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GetAsync(contentDate, cancellationToken));

    public Task<ReflectionResult<ReflectionDto>> EditAsync(
        string contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.EditAsync(contentDate, title, summary, body, cancellationToken));

    public Task<ReflectionResult<ReflectionDto>> SwitchVersionAsync(
        string contentDate,
        string versionId,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.SwitchVersionAsync(contentDate, versionId, cancellationToken));

    public Task<ReflectionResult<ReflectionDto>> ConfirmAsync(
        string contentDate,
        bool acceptedUnsourcedClaims,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ConfirmAsync(contentDate, acceptedUnsourcedClaims, cancellationToken));

    public Task<ReflectionResult<ReflectionGenerationResponse>> GenerateAsync(
        string contentDate,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GenerateAsync(
            contentDate,
            ignoreTranscriptionFailures,
            allowOverwriteOfManualEdits,
            cancellationToken));

    public Task<ReflectionResult<IReadOnlyList<PublishTargetDto>>> ListTargetsAsync(CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListTargetsAsync(cancellationToken));

    public Task<ReflectionResult<PublishResponse>> PublishAsync(
        string contentDate,
        string targetId,
        string visibility,
        bool replaceExistingFile,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.PublishAsync(contentDate, targetId, visibility, replaceExistingFile, cancellationToken));

    public Task<ReflectionResult<IReadOnlyList<PublicationDto>>> ListPublicationsAsync(
        string contentDate,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListPublicationsAsync(contentDate, cancellationToken));

    public Task<ReflectionResult<RemoteCheckResponse>> CheckRemoteAsync(
        string publicationId,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.CheckRemoteAsync(publicationId, cancellationToken));

    private async Task<ReflectionResult<T>> WithClientAsync<T>(Func<IReflectionApiClient, Task<ReflectionResult<T>>> work)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return ReflectionResult<T>.Unreachable("client.not_configured");
        }

        if (!await _tokens.HasTokenAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return ReflectionResult<T>.Unreachable("client.not_paired");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = Timeout };

        return await work(new HttpReflectionApiClient(http, _tokens)).ConfigureAwait(false);
    }
}
