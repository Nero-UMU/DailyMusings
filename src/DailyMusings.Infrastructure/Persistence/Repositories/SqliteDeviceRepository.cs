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

    /// <summary>
    /// Removes the device row.
    /// <para>
    /// 指向 <c>device(id)</c> 的引用有**两处**，删除前都得松手：
    /// </para>
    /// <list type="number">
    /// <item><c>pairing_code.redeemed_device_id</c> —— 配对码是单次使用历史，保留该行、把归属清空。第一次
    /// 在真实数据库上跑删除时就是撞在这里（用配对码创建的设备必然被它自己那张码引用）。</item>
    /// <item><c>input_entry.device_id</c> —— 「这条随想是哪台设备录的」。**内容属于实例，不属于设备**：删设备
    /// 只清空归属，绝不删内容。这一条 2026-09-29 在线上暴露：用手机录过内容的设备一删就撞外键，抛出的
    /// <c>SqliteException</c> 不是 <c>UseCaseException</c>，端点接不住 → 500 → 管理页只显示「操作没有完成，
    /// 请稍后重试」。备份写入器对同一列的处理与此一致（写备份时也会清空）。</item>
    /// </list>
    /// <para>三条语句在一个事务里：失败不能留下「码或输入指向一台已经不存在的设备」的中间状态。</para>
    /// </summary>
    public async Task DeleteAsync(DeviceId id, CancellationToken cancellationToken)
    {
        await using var transaction = await _accessor.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "UPDATE pairing_code SET redeemed_device_id = NULL WHERE redeemed_device_id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "UPDATE input_entry SET device_id = NULL WHERE device_id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

        await _accessor.ExecuteAsync(
            "DELETE FROM device WHERE id = $id;",
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

        await _accessor.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

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
