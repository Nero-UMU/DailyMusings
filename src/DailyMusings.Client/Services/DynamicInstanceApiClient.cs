using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Settings;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

/// <summary>
/// Builds the instance client per call from the address currently in settings, like the other dynamic clients.
/// This is the wrapper for the two reads that belong to the instance rather than to the capture queue: an entry's
/// stored recording, and the notification preferences (§9.3, §12, §15.2 step 6).
/// </summary>
public sealed class DynamicInstanceApiClient : INotificationSettingsApiClient, IModelNameApiClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public DynamicInstanceApiClient(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public Task<ApiResult<AudioClip>> GetAudioAsync(string inputId, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GetAudioAsync(inputId, cancellationToken));

    Task<ApiResult<NotificationSettingsDto>> INotificationSettingsApiClient.GetAsync(CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GetNotificationSettingsAsync(cancellationToken));

    Task<ApiResult<IReadOnlyList<ModelNameDto>>> IModelNameApiClient.GetAsync(CancellationToken cancellationToken) =>
        WithClientAsync(client => client.GetModelNamesAsync(cancellationToken));

    private async Task<ApiResult<T>> WithClientAsync<T>(Func<HttpInstanceApiClient, Task<ApiResult<T>>> work)
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

        return await work(new HttpInstanceApiClient(http, _tokens)).ConfigureAwait(false);
    }
}
