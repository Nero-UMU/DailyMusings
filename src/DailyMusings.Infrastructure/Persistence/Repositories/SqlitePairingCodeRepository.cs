using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Identity;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Pairing codes (docs/开发指导.md §10.2).
/// <para>
/// <see cref="TryRedeemAsync"/> is the reason this repository exists as more than a table wrapper: burning
/// the code and registering the device must be one atomic step, and the guarded <c>UPDATE … WHERE
/// used_at_utc IS NULL</c> is what makes the code genuinely single-use even when two devices redeem it at
/// the same instant.
/// </para>
/// </summary>
public sealed class SqlitePairingCodeRepository : IPairingCodeRepository
{
    private const string Columns =
        "id, code_hash, created_at_utc, expires_at_utc, used_at_utc, redeemed_device_id";

    private readonly SqliteConnectionAccessor _accessor;

    public SqlitePairingCodeRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task AddAsync(PairingCode code, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            INSERT INTO pairing_code (id, code_hash, created_at_utc, expires_at_utc, used_at_utc, redeemed_device_id)
            VALUES ($id, $codeHash, $createdAt, $expiresAt, $usedAt, $deviceId);
            """,
            cancellationToken,
            ("$id", code.Id.ToString()),
            ("$codeHash", code.CodeHash),
            ("$createdAt", SqliteValues.Instant(code.CreatedAtUtc)),
            ("$expiresAt", SqliteValues.Instant(code.ExpiresAtUtc)),
            ("$usedAt", SqliteValues.InstantOrNull(code.UsedAtUtc)),
            ("$deviceId", SqliteValues.GuidOrNull(code.RedeemedDeviceId?.Value))).ConfigureAwait(false);

    public async Task<PairingCode?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM pairing_code WHERE code_hash = $hash;",
            Map,
            cancellationToken,
            ("$hash", codeHash)).ConfigureAwait(false);

    public async Task<bool> TryRedeemAsync(
        string codeHash,
        Device device,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _accessor.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The device row has to exist before the code can point at it: pairing_code.redeemed_device_id is a
        // foreign key, and with PRAGMA foreign_keys=ON the other order fails outright. Inserting first is safe
        // because a code that turns out to be already burned rolls the whole transaction back below, taking
        // the orphan device with it.
        await _accessor.ExecuteAsync(
            """
            INSERT INTO device (id, name, token_hash, platform, created_at_utc, last_seen_at_utc, revoked_at_utc)
            VALUES ($id, $name, $tokenHash, $platform, $createdAt, NULL, NULL);
            """,
            cancellationToken,
            ("$id", device.Id.ToString()),
            ("$name", device.Name),
            ("$tokenHash", device.TokenHash),
            ("$platform", SqliteValues.TextOrNull(device.Platform)),
            ("$createdAt", SqliteValues.Instant(device.CreatedAtUtc))).ConfigureAwait(false);

        // Guarded claim: if another request already burned the code, zero rows change and we stop before
        // committing anything.
        var claimed = await _accessor.ExecuteAsync(
            """
            UPDATE pairing_code
               SET used_at_utc = $usedAt,
                   redeemed_device_id = $deviceId
             WHERE code_hash = $hash
               AND used_at_utc IS NULL;
            """,
            cancellationToken,
            ("$usedAt", SqliteValues.Instant(at)),
            ("$deviceId", device.Id.ToString()),
            ("$hash", codeHash)).ConfigureAwait(false);

        if (claimed == 0)
        {
            await _accessor.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await _accessor.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static PairingCode Map(SqliteDataReader reader) =>
        PairingCode.Rehydrate(
            SqliteIds.PairingCode(reader.GetString(0)),
            reader.GetString(1),
            SqliteValues.ReadRequiredInstant(reader, 2),
            SqliteValues.ReadRequiredInstant(reader, 3),
            SqliteValues.ReadInstant(reader, 4),
            reader.IsDBNull(5) ? null : SqliteIds.Device(reader.GetString(5)));
}
