using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using DailyMusings.Infrastructure.Persistence.Repositories;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// docs/开发指导.md §10.2：设备记录只能在**撤回之后**删除，而「删除」必须先把所有指向它的引用松手。
/// <para>
/// 这是 2026-09-29 线上暴露的缺陷的回归测试：删一台被 <c>input_entry.device_id</c> 引用着的设备（也就是
/// **用手机录过内容**的设备）会撞外键约束，抛出的 <c>SqliteException</c> 不是 <c>UseCaseException</c>，
/// 端点的 catch 接不住 → 500 → 管理页只能显示「操作没有完成，请稍后重试；如果问题持续，请查看系统设置中的
/// 运行日志」。设备是「谁采的」，内容是实例的——删设备清空归属，不能把内容一起带走。
/// </para>
/// </summary>
[TestClass]
public sealed class DeviceDeletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Task InsertInputAsync(TestDatabase database, DeviceId deviceId) =>
        database.Accessor.ExecuteAsync(
            """
            INSERT INTO input_entry (
                id, source_type, created_at_utc, created_offset_minutes, content_date,
                transcription_status, allow_future_recall, device_id)
            VALUES ('input-1', 0, '2026-03-01T12:00:00.0000000+00:00', 480, '2026-03-01', 3, 0, $device);
            """,
            CancellationToken.None,
            ("$device", deviceId.ToString()));

    [TestMethod]
    public async Task Deleting_a_revoked_device_that_captured_content_keeps_the_content()
    {
        await using var database = await TestDatabase.CreateAsync();
        var devices = new SqliteDeviceRepository(database.Accessor);

        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash", "android", Now);
        await devices.AddAsync(device, CancellationToken.None);
        await InsertInputAsync(database, device.Id);

        device.Revoke(Now.AddMinutes(1));
        await devices.UpdateAsync(device, CancellationToken.None);

        await devices.DeleteAsync(device.Id, CancellationToken.None);

        Assert.AreEqual(0L, await database.CountAsync("device"), "设备记录应当被删掉。");
        Assert.AreEqual(1L, await database.CountAsync("input_entry"), "内容属于实例，不能跟着设备一起消失。");

        var attribution = await database.Accessor.QuerySingleAsync(
            "SELECT device_id FROM input_entry WHERE id = 'input-1';",
            reader => reader.IsDBNull(0) ? null : reader.GetString(0),
            CancellationToken.None);

        Assert.IsNull(attribution, "归属必须被清空，否则这条输入指向一台已经不存在的设备。");
    }

    /// <summary>同一台设备被配对码引用着时也必须能删（这一条在更早的一次修复里已经成立，钉住它别回退）。</summary>
    [TestMethod]
    public async Task Deleting_a_revoked_device_that_came_from_pairing_keeps_the_code_row()
    {
        await using var database = await TestDatabase.CreateAsync();
        var devices = new SqliteDeviceRepository(database.Accessor);
        var codes = new SqlitePairingCodeRepository(database.Accessor);

        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash", "android", Now);
        await codes.AddAsync(PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now), CancellationToken.None);
        Assert.IsTrue(await codes.TryRedeemAsync("hash-abc", device, Now, CancellationToken.None));

        device.Revoke(Now.AddMinutes(1));
        await devices.UpdateAsync(device, CancellationToken.None);

        await devices.DeleteAsync(device.Id, CancellationToken.None);

        Assert.AreEqual(0L, await database.CountAsync("device"));
        Assert.AreEqual(1L, await database.CountAsync("pairing_code"), "配对码是单次使用历史，删除设备时保留。");

        var stored = await codes.FindByCodeHashAsync("hash-abc", CancellationToken.None);
        Assert.IsNull(stored!.RedeemedDeviceId, "配对码不能继续指向一台已经不存在的设备。");
    }
}
