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
    /// <summary>
    /// Generous on purpose: a voice upload waits for the recognition model, and the server holds the connection for
    /// up to its own inline timeout (90 seconds by default). A shorter client timeout would turn a working
    /// transcription into a "failed" upload.
    /// </summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>Reads one entry back, so a recording whose text was still being produced can be completed.</summary>
    public async Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return null;
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = ReadTimeout };
        return await new HttpCaptureApiClient(http, _tokens)
            .GetInputAsync(serverInputId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads recent entries, used to fill in recordings whose inline transcription had not finished. The calendar
    /// itself lists the device's own records, so this is not on any screen's critical path.
    /// </summary>
    public async Task<ApiResult<IReadOnlyList<InputDto>>> GetInputsAsync(string? contentDate, CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return ApiResult<IReadOnlyList<InputDto>>.Unreachable("client.not_configured");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = ReadTimeout };

        // The read path classifies its own failures; a token that is missing or rejected comes back as a result
        // rather than an exception, because a screen has to survive that.
        return await new HttpCaptureApiClient(http, _tokens)
            .GetInputsAsync(contentDate, cancellationToken)
            .ConfigureAwait(false);
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
