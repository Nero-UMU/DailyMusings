using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Topics;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

/// <summary>
/// Builds the topic client per call from the address currently in settings, like the other dynamic clients.
/// </summary>
public sealed class DynamicTopicApiClient : ITopicApiClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public DynamicTopicApiClient(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public Task<ApiResult<IReadOnlyList<TopicDto>>> ListAsync(bool includeMerged, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.ListAsync(includeMerged, cancellationToken));

    public Task<ApiResult<TopicDto>> CreateAsync(string name, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.CreateAsync(name, cancellationToken));

    public Task<ApiResult<TopicDto>> RenameAsync(string topicId, string name, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.RenameAsync(topicId, name, cancellationToken));

    public Task<ApiResult<TopicMergeResponse>> MergeAsync(
        string sourceTopicId,
        string targetTopicId,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.MergeAsync(sourceTopicId, targetTopicId, cancellationToken));

    public Task<ApiResult<InputTopicAssignmentResponse>> AssignAsync(
        string inputId,
        string? primaryTopicId,
        IReadOnlyList<string> secondaryTopicIds,
        CancellationToken cancellationToken) =>
        WithClientAsync(client => client.AssignAsync(inputId, primaryTopicId, secondaryTopicIds, cancellationToken));

    private async Task<ApiResult<T>> WithClientAsync<T>(Func<ITopicApiClient, Task<ApiResult<T>>> work)
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

        return await work(new HttpTopicApiClient(http, _tokens)).ConfigureAwait(false);
    }
}
