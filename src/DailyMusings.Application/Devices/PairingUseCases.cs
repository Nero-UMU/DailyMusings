using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Application.Devices;

/// <summary>The code to display or encode as a QR image, plus when it stops working.</summary>
public sealed record IssuedPairingCode(string Code, DateTimeOffset ExpiresAtUtc);

/// <summary>The device identity handed to a freshly paired client. The token is shown once.</summary>
public sealed record RedeemedDevice(DeviceId DeviceId, string DeviceName, string Token);

/// <summary>Mints a ten-minute, single-use pairing code (docs/开发指导.md §10.2).</summary>
public sealed class IssuePairingCodeUseCase
{
    private readonly IPairingCodeRepository _codes;
    private readonly ISecretGenerator _secretGenerator;
    private readonly IClock _clock;

    public IssuePairingCodeUseCase(
        IPairingCodeRepository codes,
        ISecretGenerator secretGenerator,
        IClock clock)
    {
        _codes = codes;
        _secretGenerator = secretGenerator;
        _clock = clock;
    }

    public async Task<IssuedPairingCode> ExecuteAsync(CancellationToken cancellationToken)
    {
        var secret = _secretGenerator.GeneratePairingCode();
        var code = PairingCode.Issue(PairingCodeId.New(), secret.Hash, _clock.UtcNow);

        await _codes.AddAsync(code, cancellationToken).ConfigureAwait(false);

        // Only the plaintext leaves this method; the store holds nothing but the hash.
        return new IssuedPairingCode(secret.Plaintext, code.ExpiresAtUtc);
    }
}

/// <summary>
/// Exchanges a pairing code for a device token.
/// <para>
/// The device row and the burnt code are written in one transaction, so a crash can never leave a usable
/// code sitting beside an already-registered device (which would let the code be replayed).
/// </para>
/// </summary>
public sealed class RedeemPairingCodeUseCase
{
    private readonly IPairingCodeRepository _codes;
    private readonly ISecretGenerator _secretGenerator;
    private readonly IClock _clock;

    public RedeemPairingCodeUseCase(
        IPairingCodeRepository codes,
        ISecretGenerator secretGenerator,
        IClock clock)
    {
        _codes = codes;
        _secretGenerator = secretGenerator;
        _clock = clock;
    }

    public async Task<RedeemedDevice> ExecuteAsync(
        string code,
        string deviceName,
        string? platform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        var now = _clock.UtcNow;
        var codeHash = _secretGenerator.HashPairingCode(code);

        var stored = await _codes.FindByCodeHashAsync(codeHash, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("pairing.code.unknown", "配对码无效。");

        // Specific, actionable failures: expired and already-used are different user problems.
        stored.EnsureRedeemable(now);

        var token = _secretGenerator.GenerateDeviceToken();
        var device = Device.Register(DeviceId.New(), deviceName, token.Hash, platform, now);

        var redeemed = await _codes
            .TryRedeemAsync(codeHash, device, now, cancellationToken)
            .ConfigureAwait(false);

        if (!redeemed)
        {
            // Another request burned the code between the read and the write.
            throw new DomainException("pairing.code.already_used", "这个配对码已经被用过了。");
        }

        return new RedeemedDevice(device.Id, device.Name, token.Plaintext);
    }
}
