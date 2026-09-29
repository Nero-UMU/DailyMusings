using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Admin;
using DailyMusings.Application.Devices;
using DailyMusings.Application.System;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// The phase-one use cases: bootstrap, sign-in, forced credential change, pairing and device administration
/// (docs/开发指导.md §10.1, §10.2).
/// </summary>
[TestClass]
public class AdminUseCaseTests
{
    [TestMethod]
    public async Task Bootstrap_creates_the_account_once_and_returns_the_password_once()
    {
        var harness = new TestHarness();
        var useCase = new InitializeAdminUseCase(
            harness.Accounts,
            harness.PasswordHasher,
            harness.SecretGenerator,
            harness.Clock);

        var first = await useCase.ExecuteAsync(CancellationToken.None);

        Assert.IsTrue(first.Created);
        Assert.AreEqual(AdminAccount.InitialUsername, first.Username);
        Assert.IsNotNull(first.GeneratedPassword);
        AdminAccount.ValidatePassword(first.GeneratedPassword);

        var second = await useCase.ExecuteAsync(CancellationToken.None);

        Assert.IsFalse(second.Created);
        Assert.IsNull(second.GeneratedPassword, "The generated password must be shown exactly once.");
    }

    [TestMethod]
    public async Task The_stored_password_is_a_hash_never_the_plaintext()
    {
        var harness = new TestHarness();
        var result = await new InitializeAdminUseCase(
                harness.Accounts, harness.PasswordHasher, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        var account = await harness.Accounts.GetAsync(CancellationToken.None);

        Assert.IsNotNull(account);
        Assert.AreNotEqual(result.GeneratedPassword, account.PasswordHash, "The plaintext must never be stored.");

        // The use case hashes through the port, and only through the hash can the password be checked again.
        // The concrete algorithm and its cost are asserted in the Infrastructure suite.
        Assert.IsTrue(harness.PasswordHasher.Verify(result.GeneratedPassword!, account.PasswordHash));
    }

    [TestMethod]
    public async Task Sign_in_distinguishes_a_missing_account_from_bad_credentials()
    {
        var harness = new TestHarness();
        var signIn = new AdminSignInUseCase(harness.Accounts, harness.PasswordHasher);

        var notInitialized = await signIn.ExecuteAsync("admin", "whatever", CancellationToken.None);
        Assert.AreEqual(SignInOutcome.NotInitialized, notInitialized.Outcome);

        var password = await BootstrapAsync(harness);

        var good = await signIn.ExecuteAsync("admin", password, CancellationToken.None);
        Assert.AreEqual(SignInOutcome.Succeeded, good.Outcome);
        Assert.IsTrue(good.MustChangeCredentials, "The first login must be forced to change credentials.");

        Assert.AreEqual(
            SignInOutcome.InvalidCredentials,
            (await signIn.ExecuteAsync("admin", "wrong-password", CancellationToken.None)).Outcome);

        Assert.AreEqual(
            SignInOutcome.InvalidCredentials,
            (await signIn.ExecuteAsync("not-admin", password, CancellationToken.None)).Outcome);

        Assert.AreEqual(
            SignInOutcome.InvalidCredentials,
            (await signIn.ExecuteAsync("admin", string.Empty, CancellationToken.None)).Outcome);
    }

    [TestMethod]
    public async Task Changing_credentials_requires_the_current_password()
    {
        var harness = new TestHarness();
        var password = await BootstrapAsync(harness);
        var change = new ChangeAdminCredentialsUseCase(harness.Accounts, harness.PasswordHasher, harness.Clock);

        var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            change.ExecuteAsync("wrong", "owner", "CorrectHorseBattery1", CancellationToken.None));

        Assert.AreEqual("admin.current_password.invalid", failure.Code);

        await change.ExecuteAsync(password, "owner", "CorrectHorseBattery1", CancellationToken.None);

        var account = await harness.Accounts.GetAsync(CancellationToken.None);
        Assert.AreEqual("owner", account!.Username);
        Assert.IsFalse(account.MustChangePassword);
        Assert.IsTrue(harness.PasswordHasher.Verify("CorrectHorseBattery1", account.PasswordHash));
    }

    [TestMethod]
    public async Task Changing_credentials_enforces_the_domain_rules()
    {
        var harness = new TestHarness();
        var password = await BootstrapAsync(harness);
        var change = new ChangeAdminCredentialsUseCase(harness.Accounts, harness.PasswordHasher, harness.Clock);

        Assert.AreEqual(
            "admin.password.too_short",
            (await Assert.ThrowsExceptionAsync<DomainException>(() =>
                change.ExecuteAsync(password, "owner", "short", CancellationToken.None))).Code);

        Assert.AreEqual(
            "admin.username.charset",
            (await Assert.ThrowsExceptionAsync<DomainException>(() =>
                change.ExecuteAsync(password, "own er", "CorrectHorseBattery1", CancellationToken.None))).Code);

        // The well-known bootstrap username must not survive the forced change.
        await change.ExecuteAsync(password, "owner", "CorrectHorseBattery1", CancellationToken.None);
        Assert.AreEqual(
            "admin.current_password.invalid",
            (await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
                change.ExecuteAsync(password, "owner2", "AnotherLongPassword1", CancellationToken.None))).Code);
    }

    /// <summary>
    /// The deployment may state the first administrator password instead of waiting for the one-time banner. It is
    /// used as given, and it is never returned as a "generated" value the host would then echo into container
    /// output — the operator already has it.
    /// </summary>
    [TestMethod]
    public async Task A_password_from_the_deployment_is_used_and_is_not_reported_as_generated()
    {
        var harness = new TestHarness();
        const string Configured = "FromTheDeployment1";

        var result = await new InitializeAdminUseCase(
                harness.Accounts, harness.PasswordHasher, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None, Configured);

        Assert.IsTrue(result.Created);
        Assert.IsTrue(result.PasswordFromDeployment);
        Assert.IsNull(result.GeneratedPassword, "Nothing was generated, so there is nothing for the host to print.");

        var account = await harness.Accounts.GetAsync(CancellationToken.None);

        Assert.IsNotNull(account);
        Assert.IsTrue(harness.PasswordHasher.Verify(Configured, account.PasswordHash));
    }

    /// <summary>
    /// A configured password that does not clear the domain rule must not create an account nobody can sign into,
    /// and must not lower the rule either: the generator takes over, and the host says so out loud.
    /// </summary>
    [TestMethod]
    public async Task An_unusable_configured_password_falls_back_to_the_generated_one()
    {
        var harness = new TestHarness();

        var result = await new InitializeAdminUseCase(
                harness.Accounts, harness.PasswordHasher, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None, "short");

        Assert.IsTrue(result.Created);
        Assert.IsFalse(result.PasswordFromDeployment);
        Assert.IsNotNull(result.GeneratedPassword);

        // The same bar a user-chosen password has to clear (§10.1).
        AdminAccount.ValidatePassword(result.GeneratedPassword!);

        var account = await harness.Accounts.GetAsync(CancellationToken.None);

        Assert.IsNotNull(account);
        Assert.IsTrue(harness.PasswordHasher.Verify(result.GeneratedPassword!, account.PasswordHash));
        Assert.IsFalse(
            harness.PasswordHasher.Verify("short", account.PasswordHash),
            "The refused value must not be what the account was created with.");
    }

    private static async Task<string> BootstrapAsync(TestHarness harness)
    {
        var result = await new InitializeAdminUseCase(
                harness.Accounts, harness.PasswordHasher, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        return result.GeneratedPassword!;
    }
}

[TestClass]
public class PairingAndDeviceUseCaseTests
{
    [TestMethod]
    public async Task A_pairing_code_is_redeemable_exactly_once()
    {
        var harness = new TestHarness();
        var issued = await new IssuePairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(harness.Clock.UtcNow + PairingCode.Lifetime, issued.ExpiresAtUtc);

        var redeem = new RedeemPairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock);
        var device = await redeem.ExecuteAsync(issued.Code, "Pixel 8", "android", CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(device.Token));
        Assert.AreEqual(1, (await harness.Devices.ListAsync(CancellationToken.None)).Count);

        var replay = await Assert.ThrowsExceptionAsync<DomainException>(() =>
            redeem.ExecuteAsync(issued.Code, "Laptop", "windows", CancellationToken.None));

        Assert.AreEqual("pairing.code.already_used", replay.Code);
        Assert.AreEqual(1, (await harness.Devices.ListAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task A_code_is_accepted_regardless_of_casing_or_the_visual_separator()
    {
        var harness = new TestHarness();
        var issued = await new IssuePairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        var retyped = issued.Code.ToLowerInvariant().Replace("-", string.Empty);

        var device = await new RedeemPairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(retyped, "Pixel 8", null, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(device.Token));
    }

    [TestMethod]
    public async Task An_expired_code_is_refused_with_its_own_reason()
    {
        var harness = new TestHarness();
        var issued = await new IssuePairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        harness.Clock.UtcNow = issued.ExpiresAtUtc;

        var failure = await Assert.ThrowsExceptionAsync<DomainException>(() =>
            new RedeemPairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
                .ExecuteAsync(issued.Code, "Pixel 8", null, CancellationToken.None));

        Assert.AreEqual("pairing.code.expired", failure.Code);
    }

    [TestMethod]
    public async Task An_unknown_code_is_refused()
    {
        var harness = new TestHarness();

        var failure = await Assert.ThrowsExceptionAsync<DomainException>(() =>
            new RedeemPairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
                .ExecuteAsync("ZZZZ-ZZZZ", "Pixel 8", null, CancellationToken.None));

        Assert.AreEqual("pairing.code.unknown", failure.Code);
    }

    [TestMethod]
    public async Task A_registered_device_authenticates_with_its_token_and_reports_activity()
    {
        var harness = new TestHarness();
        var (deviceId, token) = await PairAsync(harness);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var authenticated = await new AuthenticateDeviceUseCase(
                harness.Devices, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(token, CancellationToken.None);

        Assert.IsNotNull(authenticated);
        Assert.AreEqual(deviceId, authenticated.Id);
        Assert.AreEqual(harness.Clock.UtcNow, authenticated.LastSeenAtUtc);
    }

    [TestMethod]
    public async Task Unknown_empty_and_revoked_tokens_all_fail_authentication()
    {
        var harness = new TestHarness();
        var (deviceId, token) = await PairAsync(harness);
        var authenticate = new AuthenticateDeviceUseCase(harness.Devices, harness.SecretGenerator, harness.Clock);

        Assert.IsNull(await authenticate.ExecuteAsync("not-a-token", CancellationToken.None));
        Assert.IsNull(await authenticate.ExecuteAsync("   ", CancellationToken.None));

        await new RevokeDeviceUseCase(harness.Devices, harness.Clock).ExecuteAsync(deviceId, CancellationToken.None);

        Assert.IsNull(await authenticate.ExecuteAsync(token, CancellationToken.None));
    }

    [TestMethod]
    public async Task Rotating_a_token_invalidates_the_previous_one()
    {
        var harness = new TestHarness();
        var (deviceId, oldToken) = await PairAsync(harness);

        var rotated = await new RotateDeviceTokenUseCase(harness.Devices, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(deviceId, CancellationToken.None);

        var authenticate = new AuthenticateDeviceUseCase(harness.Devices, harness.SecretGenerator, harness.Clock);

        Assert.IsNull(await authenticate.ExecuteAsync(oldToken, CancellationToken.None));
        Assert.IsNotNull(await authenticate.ExecuteAsync(rotated.Token, CancellationToken.None));
    }

    [TestMethod]
    public async Task Operating_on_an_unknown_device_fails_with_a_specific_code()
    {
        var harness = new TestHarness();
        var missing = DeviceId.New();

        Assert.AreEqual(
            "device.unknown",
            (await Assert.ThrowsExceptionAsync<DomainException>(() =>
                new RevokeDeviceUseCase(harness.Devices, harness.Clock).ExecuteAsync(missing, CancellationToken.None))).Code);

        Assert.AreEqual(
            "device.unknown",
            (await Assert.ThrowsExceptionAsync<DomainException>(() =>
                new RotateDeviceTokenUseCase(harness.Devices, harness.SecretGenerator, harness.Clock)
                    .ExecuteAsync(missing, CancellationToken.None))).Code);
    }

    private static async Task<(DeviceId DeviceId, string Token)> PairAsync(TestHarness harness)
    {
        var issued = await new IssuePairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        var device = await new RedeemPairingCodeUseCase(harness.PairingCodes, harness.SecretGenerator, harness.Clock)
            .ExecuteAsync(issued.Code, "Pixel 8", "android", CancellationToken.None);

        return (device.DeviceId, device.Token);
    }
}

[TestClass]
public class SystemHealthUseCaseTests
{
    [TestMethod]
    public async Task Health_is_healthy_only_when_every_probe_passes()
    {
        var harness = new TestHarness();

        var healthy = await new SystemHealthUseCase(
                [new StubHealthProbe("database.writable", true), new StubHealthProbe("media.writable", true)],
                new StubMigrationRunner(),
                harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        Assert.IsTrue(healthy.Healthy);
        Assert.AreEqual(harness.Clock.UtcNow, healthy.CheckedAtUtc);
        CollectionAssert.Contains(healthy.Probes.Select(p => p.Name).ToArray(), "database.migrations");

        var unhealthy = await new SystemHealthUseCase(
                [new StubHealthProbe("database.writable", true), new StubHealthProbe("media.writable", false)],
                new StubMigrationRunner(),
                harness.Clock)
            .ExecuteAsync(CancellationToken.None);

        Assert.IsFalse(unhealthy.Healthy);
    }
}
