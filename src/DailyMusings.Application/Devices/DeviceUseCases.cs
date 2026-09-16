using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Application.Devices;

public sealed record RotatedDeviceToken(DeviceId DeviceId, string Token);

/// <summary>Lists paired devices for the admin page. Token hashes never leave the server.</summary>
public sealed class ListDevicesUseCase
{
    private readonly IDeviceRepository _devices;

    public ListDevicesUseCase(IDeviceRepository devices) => _devices = devices;

    public Task<IReadOnlyList<Device>> ExecuteAsync(CancellationToken cancellationToken) =>
        _devices.ListAsync(cancellationToken);
}

/// <summary>Revokes one device without touching the others (§10.2).</summary>
public sealed class RevokeDeviceUseCase
{
    private readonly IDeviceRepository _devices;
    private readonly IClock _clock;

    public RevokeDeviceUseCase(IDeviceRepository devices, IClock clock)
    {
        _devices = devices;
        _clock = clock;
    }

    public async Task ExecuteAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("device.unknown", $"No device with id {deviceId}.");

        device.Revoke(_clock.UtcNow);
        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Issues a new token for one device, invalidating the old one (§10.2).</summary>
public sealed class RotateDeviceTokenUseCase
{
    private readonly IDeviceRepository _devices;
    private readonly ISecretGenerator _secretGenerator;
    private readonly IClock _clock;

    public RotateDeviceTokenUseCase(
        IDeviceRepository devices,
        ISecretGenerator secretGenerator,
        IClock clock)
    {
        _devices = devices;
        _secretGenerator = secretGenerator;
        _clock = clock;
    }

    public async Task<RotatedDeviceToken> ExecuteAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("device.unknown", $"No device with id {deviceId}.");

        var token = _secretGenerator.GenerateDeviceToken();
        device.RotateToken(token.Hash, _clock.UtcNow);

        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        return new RotatedDeviceToken(device.Id, token.Plaintext);
    }
}

/// <summary>
/// Turns a presented bearer token into a device. Called by the API's authentication handler on every
/// client request, so it stays allocation-light and never logs the token.
/// </summary>
public sealed class AuthenticateDeviceUseCase
{
    private readonly IDeviceRepository _devices;
    private readonly ISecretGenerator _secretGenerator;
    private readonly IClock _clock;

    public AuthenticateDeviceUseCase(
        IDeviceRepository devices,
        ISecretGenerator secretGenerator,
        IClock clock)
    {
        _devices = devices;
        _secretGenerator = secretGenerator;
        _clock = clock;
    }

    public async Task<Device?> ExecuteAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return null;
        }

        var hash = _secretGenerator.HashToken(presentedToken);
        var device = await _devices.FindByTokenHashAsync(hash, cancellationToken).ConfigureAwait(false);

        if (device is null || device.IsRevoked)
        {
            return null;
        }

        device.Touch(_clock.UtcNow);
        await _devices.UpdateAsync(device, cancellationToken).ConfigureAwait(false);

        return device;
    }
}
