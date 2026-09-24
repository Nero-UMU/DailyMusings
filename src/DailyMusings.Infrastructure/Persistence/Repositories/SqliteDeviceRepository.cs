using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Paired devices (docs/开发指导.md §10.2). Lookup by token hash is the hot path — it runs on every client
/// request — hence the UNIQUE index on <c>token_hash</c>.
/// </summary>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private const string Columns =
        "id, name, token_hash, platform, created_at_utc, last_seen_at_utc, revoked_at_utc";

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteDeviceRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {Columns} FROM device ORDER BY created_at_utc;",
            Map,
            cancellationToken).ConfigureAwait(false);

    public async Task<Device?> FindByIdAsync(DeviceId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM device WHERE id = $id;",
            Map,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    public async Task<Device?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM device WHERE token_hash = $hash;",
            Map,
            cancellationToken,
            ("$hash", tokenHash)).ConfigureAwait(false);

    public async Task AddAsync(Device device, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            INSERT INTO device (id, name, token_hash, platform, created_at_utc, last_seen_at_utc, revoked_at_utc)
            VALUES ($id, $name, $tokenHash, $platform, $createdAt, $lastSeen, $revokedAt);
            """,
            cancellationToken,
            ("$id", device.Id.ToString()),
            ("$name", device.Name),
            ("$tokenHash", device.TokenHash),
            ("$platform", SqliteValues.TextOrNull(device.Platform)),
            ("$createdAt", SqliteValues.Instant(device.CreatedAtUtc)),
            ("$lastSeen", SqliteValues.InstantOrNull(device.LastSeenAtUtc)),
            ("$revokedAt", SqliteValues.InstantOrNull(device.RevokedAtUtc))).ConfigureAwait(false);

    public async Task UpdateAsync(Device device, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE device
               SET name = $name,
                   token_hash = $tokenHash,
                   platform = $platform,
                   last_seen_at_utc = $lastSeen,
                   revoked_at_utc = $revokedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", device.Id.ToString()),
            ("$name", device.Name),
            ("$tokenHash", device.TokenHash),
            ("$platform", SqliteValues.TextOrNull(device.Platform)),
            ("$lastSeen", SqliteValues.InstantOrNull(device.LastSeenAtUtc)),
            ("$revokedAt", SqliteValues.InstantOrNull(device.RevokedAtUtc))).ConfigureAwait(false);

    public async Task DeleteAsync(DeviceId id, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            "DELETE FROM device WHERE id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    private static Device Map(SqliteDataReader reader) =>
        Device.Rehydrate(
            SqliteIds.Device(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            SqliteValues.ReadRequiredInstant(reader, 4),
            SqliteValues.ReadInstant(reader, 5),
            SqliteValues.ReadInstant(reader, 6));
}
