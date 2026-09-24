using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// docs/开发指导.md §10.2 and §14: a pairing code is single-use and short-lived, and redeeming it must be
/// atomic — registering the device and burning the code either both happen or neither does.
/// <para>
/// This is also the regression test for a foreign-key ordering bug: writing <c>redeemed_device_id</c> before the
/// device row exists fails outright once <c>PRAGMA foreign_keys=ON</c>, which turned pairing into a 500.
/// </para>
/// </summary>
[TestClass]
public class PairingCodeRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(TestDatabase Database, SqlitePairingCodeRepository Repository)> ArrangeAsync()
    {
        var database = await TestDatabase.CreateAsync();
        return (database, new SqlitePairingCodeRepository(database.Accessor));
    }

    [TestMethod]
    public async Task A_code_round_trips_by_hash_and_is_redeemable_once()
    {
        var (database, repository) = await ArrangeAsync();
        await using var _ = database;

        var code = PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now);
        await repository.AddAsync(code, CancellationToken.None);

        var read = await repository.FindByCodeHashAsync("hash-abc", CancellationToken.None);

        Assert.IsNotNull(read);
        Assert.AreEqual(code.Id, read.Id);
        Assert.IsTrue(read.IsRedeemable(Now));
        Assert.IsNull(await repository.FindByCodeHashAsync("hash-nope", CancellationToken.None));
    }

    [TestMethod]
    public async Task Redeeming_registers_exactly_one_device_and_commits_both_writes()
    {
        var (database, repository) = await ArrangeAsync();
        await using var _ = database;

        await repository.AddAsync(PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now), CancellationToken.None);
        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash", "android", Now);

        var redeemed = await repository.TryRedeemAsync("hash-abc", device, Now, CancellationToken.None);

        Assert.IsTrue(redeemed);
        Assert.AreEqual(1L, await database.CountAsync("device"));

        var stored = await repository.FindByCodeHashAsync("hash-abc", CancellationToken.None);
        Assert.IsTrue(stored!.IsRedeemed);
        Assert.AreEqual(device.Id, stored.RedeemedDeviceId);

        // The device is retrievable through the normal repository, i.e. the foreign key resolved.
        var devices = new SqliteDeviceRepository(database.Accessor);
        var found = await devices.FindByIdAsync(device.Id, CancellationToken.None);
        Assert.IsNotNull(found);
        Assert.AreEqual("Pixel 8", found.Name);
    }

    [TestMethod]
    public async Task Replaying_a_burned_code_creates_no_second_device()
    {
        var (database, repository) = await ArrangeAsync();
        await using var _ = database;

        await repository.AddAsync(PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now), CancellationToken.None);

        var first = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-1", "android", Now);
        var second = Device.Register(DeviceId.New(), "Laptop", "token-hash-2", "windows", Now);

        Assert.IsTrue(await repository.TryRedeemAsync("hash-abc", first, Now, CancellationToken.None));
        Assert.IsFalse(await repository.TryRedeemAsync("hash-abc", second, Now, CancellationToken.None));

        // The rolled-back attempt must not have left its device behind.
        Assert.AreEqual(1L, await database.CountAsync("device"));

        var devices = new SqliteDeviceRepository(database.Accessor);
        Assert.IsNull(await devices.FindByIdAsync(second.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task Only_one_of_two_simultaneous_redemptions_can_win()
    {
        var (database, repository) = await ArrangeAsync();
        await using var _ = database;

        await repository.AddAsync(PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now), CancellationToken.None);

        // Two independent connections, so the guard is genuinely tested rather than serialized in-process.
        var accessorA = new SqliteConnectionAccessor(database.Accessor.DatabasePath);
        var accessorB = new SqliteConnectionAccessor(database.Accessor.DatabasePath);
        await using var _a = accessorA;
        await using var _b = accessorB;

        var deviceA = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-a", "android", Now);
        var deviceB = Device.Register(DeviceId.New(), "Laptop", "token-hash-b", "windows", Now);

        var results = await Task.WhenAll(
            new SqlitePairingCodeRepository(accessorA).TryRedeemAsync("hash-abc", deviceA, Now, CancellationToken.None),
            new SqlitePairingCodeRepository(accessorB).TryRedeemAsync("hash-abc", deviceB, Now, CancellationToken.None));

        Assert.AreEqual(1, results.Count(won => won), "Exactly one redemption may succeed.");
        Assert.AreEqual(1L, await database.CountAsync("device"));
    }

    [TestMethod]
    public async Task A_revoked_device_cannot_authenticate_and_a_rotation_invalidates_the_old_token()
    {
        var (database, _) = await ArrangeAsync();
        await using var __ = database;

        var devices = new SqliteDeviceRepository(database.Accessor);
        var device = Device.Register(DeviceId.New(), "Pixel 8", "hash-old", null, Now);
        await devices.AddAsync(device, CancellationToken.None);

        Assert.IsNotNull(await devices.FindByTokenHashAsync("hash-old", CancellationToken.None));

        device.RotateToken("hash-new", Now);
        await devices.UpdateAsync(device, CancellationToken.None);

        Assert.IsNull(await devices.FindByTokenHashAsync("hash-old", CancellationToken.None));
        Assert.IsNotNull(await devices.FindByTokenHashAsync("hash-new", CancellationToken.None));

        device.Revoke(Now);
        await devices.UpdateAsync(device, CancellationToken.None);

        var revoked = await devices.FindByTokenHashAsync("hash-new", CancellationToken.None);
        Assert.IsTrue(revoked!.IsRevoked);
    }

    /// <summary>
    /// Deleting a device that was created by pairing has to let go of the pairing code first.
    /// <para>
    /// This is a regression test for a defect found on the deployed instance and nowhere else: the delete endpoint
    /// answered 500 with「FOREIGN KEY constraint failed」, because <c>pairing_code.redeemed_device_id</c> references
    /// the device row and every paired device is therefore referenced by its own code. No unit test covered the
    /// delete path against a database where that reference existed.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task A_device_created_by_pairing_can_be_deleted()
    {
        var (database, repository) = await ArrangeAsync();
        await using var _ = database;

        await repository.AddAsync(PairingCode.Issue(PairingCodeId.New(), "hash-abc", Now), CancellationToken.None);

        var devices = new SqliteDeviceRepository(database.Accessor);
        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-1", "android", Now);
        Assert.IsTrue(await repository.TryRedeemAsync("hash-abc", device, Now, CancellationToken.None));

        await devices.DeleteAsync(device.Id, CancellationToken.None);

        Assert.AreEqual(0L, await database.CountAsync("device"), "The device row is gone.");

        // The code survives as single-use history, but it no longer points at a device that does not exist.
        var stored = await repository.FindByCodeHashAsync("hash-abc", CancellationToken.None);
        Assert.IsNotNull(stored);
        Assert.IsNull(stored!.RedeemedDeviceId);
    }
}
