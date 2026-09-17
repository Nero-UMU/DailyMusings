using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Services;

public sealed record PairingResult(bool Succeeded, string? DeviceName, string? FailureMessage);

/// <summary>
/// Redeems a pairing code and stores the resulting token (docs/开发指导.md §10.2).
/// <para>
/// This is the only request the client makes without a token, which is why it builds its own short-lived HTTP call
/// rather than going through the capture client.
/// </para>
/// </summary>
public sealed class PairingService
{
    private readonly ClientSettings _settings;
    private readonly SecureDeviceTokenProvider _tokens;

    public PairingService(ClientSettings settings, SecureDeviceTokenProvider tokens)
    {
        _settings = settings;
        _tokens = tokens;
    }

    public async Task<PairingResult> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return new PairingResult(false, null, "请先在设置里填写服务器地址。");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };

        // The platform is the one this client actually runs on. It used to be the literal "android", which put every
        // Windows machine in the admin device list as an Android phone.
        var request = new RedeemPairingCodeRequest(
            code.Trim(),
            DeviceName(),
            DailyMusings.Client.Core.PlatformNames.FromRuntimeName(DeviceInfo.Current.Platform.ToString()));

        try
        {
            using var response = await http
                .PostAsJsonAsync(ApiRoutes.PairingRedeem, request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PairingResult(false, null, await DescribeFailureAsync(response, cancellationToken)
                    .ConfigureAwait(false));
            }

            var device = await response.Content
                .ReadFromJsonAsync<RedeemPairingCodeResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (device is null || string.IsNullOrWhiteSpace(device.Token))
            {
                return new PairingResult(false, null, "服务器没有返回设备令牌。");
            }

            await _tokens.StoreAsync(device.Token, cancellationToken).ConfigureAwait(false);

            return new PairingResult(true, device.DeviceName, null);
        }
        catch (HttpRequestException)
        {
            return new PairingResult(false, null, "无法连接服务器，请检查地址与网络。");
        }
        catch (TaskCanceledException)
        {
            return new PairingResult(false, null, "服务器没有及时响应。");
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (_settings.ResolveBaseUri() is not { } baseUri)
        {
            return false;
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };

        try
        {
            using var response = await http.GetAsync(ApiRoutes.Health, cancellationToken).ConfigureAwait(false);

            // 503 means the instance answered but reports itself unhealthy — still a reachable server, which is what
            // this check is about.
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>A name a human will recognise in the admin device list.</summary>
    private static string DeviceName()
    {
        var model = DeviceInfo.Current.Model;
        return string.IsNullOrWhiteSpace(model) ? "Android 设备" : model;
    }

    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ApiError>(cancellationToken)
                .ConfigureAwait(false);

            return error?.Code switch
            {
                "pairing.code.expired" => "配对码已过期，请在管理页重新生成。",
                "pairing.code.already_used" => "这个配对码已经被用过了。",
                "pairing.code.unknown" => "配对码不正确。",
                "request.invalid" => "配对码格式不正确。",
                _ => error?.Message ?? $"服务器返回 {(int)response.StatusCode}。",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"服务器返回 {(int)response.StatusCode}。";
        }
    }
}
