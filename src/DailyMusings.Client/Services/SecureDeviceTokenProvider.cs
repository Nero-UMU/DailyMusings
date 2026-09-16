using DailyMusings.Client.Core;

namespace DailyMusings.Client.Services;

/// <summary>
/// Keeps the device token in the platform's secure storage (docs/开发指导.md §10.2: 客户端使用平台安全存储保存令牌).
/// <para>
/// Deliberately never written to preferences or to a file: the token is the whole of this device's authority, and
/// §4.1 is explicit that a client stores a server reference and a device token and nothing else.
/// </para>
/// </summary>
public sealed class SecureDeviceTokenProvider : IDeviceTokenProvider
{
    private const string TokenKey = "device.token";

    private string? _cached;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            _cached = await SecureStorage.Default.GetAsync(TokenKey).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Secure storage can fail on a device with a broken keystore. Treating that as "not paired" is the
            // honest outcome: the user re-pairs instead of seeing an unexplained crash.
            return null;
        }

        return _cached;
    }

    public async Task StoreAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        await SecureStorage.Default.SetAsync(TokenKey, token).ConfigureAwait(false);
        _cached = token;
    }

    public void Clear()
    {
        SecureStorage.Default.Remove(TokenKey);
        _cached = null;
    }

    public async Task<bool> HasTokenAsync(CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(await GetTokenAsync(cancellationToken).ConfigureAwait(false));
}
