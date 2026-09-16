using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Identity;

/// <summary>
/// docs/开发指导.md §10.1 and §10.2: a first-login credential change that cannot be skipped, ten-minute
/// single-use pairing codes, and device tokens that are individually rotatable and revocable.
/// </summary>
[TestClass]
public class IdentityTests
{
    private static AdminAccount InitialAdmin() =>
        AdminAccount.CreateInitial(AdminAccountId.New(), "pbkdf2-sha256$210000$c2FsdA==$aGFzaA==", TestFactory.Noon);

    [TestMethod]
    public void The_initial_account_is_admin_and_must_change_its_credentials()
    {
        var account = InitialAdmin();

        Assert.AreEqual("admin", account.Username);
        Assert.IsTrue(account.MustChangePassword);
        Assert.IsNull(account.CredentialsChangedAtUtc);
    }

    [TestMethod]
    public void Changing_credentials_replaces_both_the_name_and_the_password()
    {
        var account = InitialAdmin();

        account.ChangeCredentials("owner", "pbkdf2-sha256$210000$bmV3$bmV3aGFzaA==", TestFactory.Noon);

        Assert.AreEqual("owner", account.Username);
        Assert.IsFalse(account.MustChangePassword);
        Assert.AreEqual(TestFactory.Noon, account.CredentialsChangedAtUtc);
    }

    [TestMethod]
    public void Username_and_password_rules_are_enforced()
    {
        TestFactory.ThrowsDomain("admin.username.length", () => AdminAccount.ValidateUsername("ab"));
        TestFactory.ThrowsDomain("admin.username.charset", () => AdminAccount.ValidateUsername("owner name"));
        TestFactory.ThrowsDomain("admin.password.too_short", () => AdminAccount.ValidatePassword("short"));

        AdminAccount.ValidateUsername("owner_1");
        AdminAccount.ValidatePassword(new string('x', AdminAccount.MinPasswordLength));
    }

    [TestMethod]
    public void A_pairing_code_is_single_use_and_expires_after_ten_minutes()
    {
        var issuedAt = TestFactory.Noon;
        var code = PairingCode.Issue(PairingCodeId.New(), "hash-of-code", issuedAt);

        Assert.AreEqual(TimeSpan.FromMinutes(10), PairingCode.Lifetime);
        Assert.AreEqual(issuedAt + PairingCode.Lifetime, code.ExpiresAtUtc);

        Assert.IsTrue(code.IsRedeemable(issuedAt));
        Assert.IsTrue(code.IsRedeemable(code.ExpiresAtUtc.AddTicks(-1)));
        Assert.IsFalse(code.IsRedeemable(code.ExpiresAtUtc));

        TestFactory.ThrowsDomain("pairing.code.expired", () => code.EnsureRedeemable(code.ExpiresAtUtc));
    }

    [TestMethod]
    public void A_redeemed_pairing_code_cannot_be_used_again()
    {
        var code = PairingCode.Issue(PairingCodeId.New(), "hash-of-code", TestFactory.Noon);
        var deviceId = DeviceId.New();

        code.Redeem(deviceId, TestFactory.Noon);

        Assert.IsTrue(code.IsRedeemed);
        Assert.AreEqual(deviceId, code.RedeemedDeviceId);
        Assert.IsFalse(code.IsRedeemable(TestFactory.Noon.AddSeconds(1)));

        TestFactory.ThrowsDomain(
            "pairing.code.already_used",
            () => code.Redeem(DeviceId.New(), TestFactory.Noon.AddSeconds(1)));
    }

    [TestMethod]
    public void A_device_records_activity_and_can_be_revoked_on_its_own()
    {
        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-a", "android", TestFactory.Noon);

        device.Touch(TestFactory.Noon.AddMinutes(5));
        Assert.AreEqual(TestFactory.Noon.AddMinutes(5), device.LastSeenAtUtc);
        Assert.IsFalse(device.IsRevoked);

        device.Revoke(TestFactory.Noon.AddMinutes(10));

        Assert.IsTrue(device.IsRevoked);
        TestFactory.ThrowsDomain("device.revoked", () => device.Touch(TestFactory.Noon.AddMinutes(11)));
        TestFactory.ThrowsDomain("device.revoked", () => device.RotateToken("token-hash-b", TestFactory.Noon.AddMinutes(11)));
    }

    [TestMethod]
    public void Revoking_a_device_twice_is_harmless()
    {
        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-a", "android", TestFactory.Noon);

        device.Revoke(TestFactory.Noon);
        device.Revoke(TestFactory.Noon.AddMinutes(1));

        Assert.AreEqual(TestFactory.Noon, device.RevokedAtUtc);
    }

    [TestMethod]
    public void Rotating_a_token_invalidates_the_previous_one()
    {
        var device = Device.Register(DeviceId.New(), "Pixel 8", "token-hash-a", "android", TestFactory.Noon);

        device.RotateToken("token-hash-b", TestFactory.Noon.AddMinutes(30));

        Assert.AreEqual("token-hash-b", device.TokenHash);
        Assert.AreNotEqual("token-hash-a", device.TokenHash);
    }
}
