using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Client.Core;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Http;

/// <summary>
/// Talks to the server's capture endpoints (docs/开发指导.md §8.2, §13).
/// <para>
/// Every failure leaves here classified as transient or not, because that classification is the difference between
/// the queue retrying quietly and the user being asked to do something. An authentication failure is deliberately
/// not transient: retrying a rejected device token forever would hide the fact that the device needs re-pairing.
/// </para>
/// </summary>
public sealed class HttpCaptureApiClient : ICaptureApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IDeviceTokenProvider _tokenProvider;

    public HttpCaptureApiClient(HttpClient httpClient, IDeviceTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    public async Task<IngestResponse> UploadVoiceAsync(VoiceUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var audio = new StreamContent(OpenAudio(upload.LocalAudioPath));
        audio.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);

        using var form = new MultipartFormDataContent
        {
            { audio, VoiceUploadFields.Audio, Path.GetFileName(upload.LocalAudioPath) },
            { new StringContent(IsoFormat(upload.CreatedAtUtc)), VoiceUploadFields.CreatedAtUtc },
            { new StringContent(upload.CreatedOffsetMinutes.ToString(CultureInfo.InvariantCulture)), VoiceUploadFields.CreatedOffsetMinutes },
            { new StringContent(upload.IdempotencyKey), VoiceUploadFields.IdempotencyKey },
        };

        if (upload.DurationSeconds is { } seconds && seconds >= 0)
        {
            form.Add(
                new StringContent(((long)(seconds * 1000)).ToString(CultureInfo.InvariantCulture)),
                VoiceUploadFields.DurationMilliseconds);
        }

        return await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "/api/inputs/voice") { Content = form },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IngestResponse> UploadTextAsync(TextUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var body = new TextInputRequest(
            upload.Text,
            IsoFormat(upload.CreatedAtUtc),
            upload.CreatedOffsetMinutes,
            upload.IdempotencyKey);

        return await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "/api/inputs/text")
                {
                    Content = JsonContent.Create(body),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverInputId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inputs/{serverInputId}");
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<InputDto>(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a day's entries without ever throwing on a transport failure (docs/开发指导.md §9.2).
    /// <para>
    /// This is the one read the capture screen makes, and it runs while the screen is appearing — an exception here
    /// used to escape an <c>async void</c> handler and kill the app. It is also a read that must not lie: an empty
    /// list means "the server has nothing for today", so a server that could not be reached has to be reported as
    /// exactly that rather than as an empty day.
    /// </para>
    /// </summary>
    public async Task<InputListResult> GetInputsAsync(string contentDate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDate);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/inputs?date={Uri.EscapeDataString(contentDate)}");

        try
        {
            await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (CaptureUploadException exception)
        {
            // Not paired: there is nothing to read, and the screen says so in its own words.
            return InputListResult.Unreachable(exception.Code);
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return InputListResult.Unreachable("client.network_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InputListResult.Unreachable("client.timeout");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return InputListResult.Refused("auth.device_token_rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return InputListResult.Refused($"server.rejected.{(int)response.StatusCode}");
            }

            try
            {
                var page = await response.Content
                    .ReadFromJsonAsync<InputListResponse>(cancellationToken)
                    .ConfigureAwait(false);

                return InputListResult.FromServer(page?.Items ?? []);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return InputListResult.Refused("client.malformed_response");
            }
        }
    }

    private async Task<IngestResponse> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var ingested = await response.Content
            .ReadFromJsonAsync<IngestResponse>(cancellationToken)
            .ConfigureAwait(false);

        return ingested ?? throw new CaptureUploadException(
            "client.empty_response",
            "The server returned an empty response.",
            transient: true);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // No answer at all: the network, not the server, is the problem. The capture stays queued.
            throw new CaptureUploadException(
                "client.network_unreachable",
                "The server could not be reached.",
                transient: true,
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CaptureUploadException(
                "client.timeout",
                "The server did not answer in time.",
                transient: true,
                exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Permanent on purpose: a rejected token needs the device to be paired again, and retrying would hide it.
            throw new CaptureUploadException(
                code ?? "auth.device_token_rejected",
                "This device is no longer authorized. Pair it again.",
                transient: false);
        }

        var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;

        throw new CaptureUploadException(
            code ?? "client.upload_rejected",
            $"The server rejected the upload ({(int)response.StatusCode}).",
            transient);
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

            return error?.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // An error body we cannot parse is not itself a failure worth reporting; the status code stands.
            return null;
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CaptureUploadException(
                "client.not_paired",
                "This device has not been paired with the server yet.",
                transient: false);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static Stream OpenAudio(string path)
    {
        if (!File.Exists(path))
        {
            // The local copy is gone, so this capture can never be uploaded. Permanent, so it stops being retried.
            throw new CaptureUploadException(
                "client.audio_missing",
                "The local recording is no longer on this device.",
                transient: false);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
    }

    private static string IsoFormat(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
