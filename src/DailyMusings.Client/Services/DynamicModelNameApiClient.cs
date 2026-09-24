using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Settings;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

/// <summary>
/// Builds the model-name client per call from the address currently in settings, like the capture client.
/// This is the only read the phone still makes about the instance itself (§8.1): the model names it is configured
/// with, and whether each one is switched on.
/// </summary>
public sealed class DynamicModelNameApiClient : IModelNameApiClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public DynamicModelNameApiClient(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public async Task<ApiResult<IReadOnlyList<ModelNameDto>>> GetAsync(CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.not_configured");
        }

        if (!await _tokens.HasTokenAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return ApiResult<IReadOnlyList<ModelNameDto>>.Unreachable("client.not_paired");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = Timeout };

        return await new HttpModelNameApiClient(http, _tokens)
            .GetModelNamesAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
