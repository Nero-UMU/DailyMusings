using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Identity;

/// <summary>
/// A short-lived, single-use code the admin page displays for a device to redeem
/// (docs/开发指导.md §10.2, decision A.13 keeps it out of backups).
/// </summary>
public sealed class PairingCode
{
    /// <summary>Ten minutes, as specified in §10.2.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private PairingCode(
        PairingCodeId id,
        string codeHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        CodeHash = codeHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public PairingCodeId Id { get; }

    /// <summary>Only the hash is persisted; the code itself is shown to the administrator once.</summary>
    public string CodeHash { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public DeviceId? RedeemedDeviceId { get; private set; }

    public bool IsRedeemed => UsedAtUtc is not null;

    public static PairingCode Issue(PairingCodeId id, string codeHash, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        return new PairingCode(id, codeHash, at, at + Lifetime);
    }

    public static PairingCode Rehydrate(
        PairingCodeId id,
        string codeHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? usedAtUtc,
        DeviceId? redeemedDeviceId) =>
        new(id, codeHash, createdAtUtc, expiresAtUtc)
        {
            UsedAtUtc = usedAtUtc,
            RedeemedDeviceId = redeemedDeviceId,
        };

    public bool IsRedeemable(DateTimeOffset at) => !IsRedeemed && at < ExpiresAtUtc;

    /// <summary>
    /// Throws with a specific code for each failure mode, so the API can answer "expired" and "already
    /// used" differently without leaking whether a guessed code ever existed. Both branches are checked
    /// before any state changes, which is what makes redemption single-use even under concurrent calls.
    /// </summary>
    public void EnsureRedeemable(DateTimeOffset at)
    {
        if (IsRedeemed)
        {
            throw new DomainException("pairing.code.already_used", "The pairing code has already been used.");
        }

        if (at >= ExpiresAtUtc)
        {
            throw new DomainException("pairing.code.expired", "The pairing code has expired.");
        }
    }

    /// <summary>Burns the code and ties it to the device it produced.</summary>
    public void Redeem(DeviceId deviceId, DateTimeOffset at)
    {
        EnsureRedeemable(at);

        if (deviceId.IsEmpty)
        {
            throw new DomainException("pairing.code.device_required", "A redeemed code must name its device.");
        }

        UsedAtUtc = at;
        RedeemedDeviceId = deviceId;
    }
}
