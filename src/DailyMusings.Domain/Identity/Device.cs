using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Identity;

/// <summary>
/// A paired client device (docs/开发指导.md §10.2).
/// <para>
/// Only the hash of the device token is ever stored — the token itself lives in the platform secure
/// storage on the device, and is shown to the server exactly once. Tokens are individually revocable
/// and rotatable, and the plaintext token never reaches a log or a backup (§10.4, decision A.13).
/// </para>
/// </summary>
public sealed class Device
{
    private Device(
        DeviceId id,
        string name,
        string tokenHash,
        string? platform,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        TokenHash = tokenHash;
        Platform = platform;
        CreatedAtUtc = createdAtUtc;
    }

    public DeviceId Id { get; }

    public string Name { get; private set; }

    /// <summary>Hash of the bearer token. Unique across devices, so lookup is a single indexed probe.</summary>
    public string TokenHash { get; private set; }

    /// <summary>Free-form client description, e.g. <c>android</c> or <c>windows</c>.</summary>
    public string? Platform { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static Device Register(
        DeviceId id,
        string name,
        string tokenHash,
        string? platform,
        DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return new Device(id, name.Trim(), tokenHash, platform?.Trim(), at);
    }

    public static Device Rehydrate(
        DeviceId id,
        string name,
        string tokenHash,
        string? platform,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? lastSeenAtUtc,
        DateTimeOffset? revokedAtUtc) =>
        new(id, name, tokenHash, platform, createdAtUtc)
        {
            LastSeenAtUtc = lastSeenAtUtc,
            RevokedAtUtc = revokedAtUtc,
        };

    public void Rename(string name)
    {
        EnsureNotRevoked();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>Records authenticated activity. A revoked device must not be able to authenticate at all.</summary>
    public void Touch(DateTimeOffset at)
    {
        EnsureNotRevoked();
        LastSeenAtUtc = at;
    }

    /// <summary>Revokes this device only; other devices keep working (§10.2).</summary>
    public void Revoke(DateTimeOffset at)
    {
        if (IsRevoked)
        {
            return; // idempotent, so a repeated revoke request is harmless
        }

        RevokedAtUtc = at;
    }

    /// <summary>Re-issues the token for this device, invalidating the previous one.</summary>
    public void RotateToken(string newTokenHash, DateTimeOffset at)
    {
        EnsureNotRevoked();
        ArgumentException.ThrowIfNullOrWhiteSpace(newTokenHash);

        TokenHash = newTokenHash;
        LastSeenAtUtc = at;
    }

    private void EnsureNotRevoked()
    {
        if (IsRevoked)
        {
            throw new DomainException("device.revoked", "这台设备的授权已被撤回。");
        }
    }
}
