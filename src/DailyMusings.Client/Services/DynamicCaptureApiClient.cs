using System.Net.Http.Json;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Http;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

/// <summary>
/// Builds the real HTTP client per call, from the address currently in settings.
/// <para>
/// The alternative — one long-lived client built at start-up — would keep pointing at the old server after the user
/// corrects the address, and would need restarting the app to take effect. A phone makes a handful of uploads a day,
/// so paying for a client per call is the cheaper mistake.
/// </para>
/// </summary>
public sealed class DynamicCaptureApiClient : ICaptureApiClient
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public DynamicCaptureApiClient(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public Task<IngestResponse> UploadVoiceAsync(VoiceUpload upload, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.UploadVoiceAsync(upload, cancellationToken));

    public Task<IngestResponse> UploadTextAsync(TextUpload upload, CancellationToken cancellationToken) =>
        WithClientAsync(client => client.UploadTextAsync(upload, cancellationToken));

    public async Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return null;
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        return await new HttpCaptureApiClient(http, _tokens)
            .GetInputAsync(serverInputId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a day's entries, so the timeline reflects the server rather than a second local copy.</summary>
    public async Task<IReadOnlyList<InputDto>> GetInputsAsync(string contentDate, CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return [];
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };

        if (await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/inputs?date={contentDate}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var page = await response.Content
            .ReadFromJsonAsync<InputListResponse>(cancellationToken)
            .ConfigureAwait(false);

        return page?.Items ?? [];
    }

    private async Task<IngestResponse> WithClientAsync(Func<ICaptureApiClient, Task<IngestResponse>> work)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            throw new CaptureUploadException(
                "client.not_configured",
                "尚未配置服务器地址，请到设置页填写。",
                transient: false);
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = UploadTimeout };

        return await work(new HttpCaptureApiClient(http, _tokens)).ConfigureAwait(false);
    }
}
