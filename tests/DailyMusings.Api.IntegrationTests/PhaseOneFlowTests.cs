using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The phase-one acceptance path, driven over real HTTP against the real application: bootstrap, forced
/// credential change, pairing, device authentication and revocation
/// (docs/开发指导.md §10.1, §10.2, §16, §18).
/// <para>
/// Serialized on purpose: the bootstrap captures the process-wide <see cref="Console"/> to observe the
/// one-time password, so these tests must not overlap.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class PhaseOneFlowTests
{
    [TestMethod]
    public async Task Health_is_anonymous_reports_the_expected_probes_and_hides_detail_from_strangers()
    {
        await using var instance = await TestInstance.StartAsync();

        var response = await instance.Client.GetAsync(ApiRoutes.Health);
        var report = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(report);
        Assert.IsTrue(report.Healthy, "A freshly started instance must be healthy.");

        var names = report.Probes.Select(probe => probe.Name).ToArray();
        CollectionAssert.Contains(names, "database.writable");
        CollectionAssert.Contains(names, "media.writable");
        CollectionAssert.Contains(names, "jobExecutor.alive");
        CollectionAssert.Contains(names, "database.migrations");

        // §16 keeps external services out of basic health; they get their own "test connection".
        Assert.IsFalse(names.Any(name => name.Contains("model", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(names.Any(name => name.Contains("smtp", StringComparison.OrdinalIgnoreCase)));

        Assert.IsTrue(
            report.Probes.All(probe => probe.Detail is null),
            "Probe details can name internal paths, so an anonymous caller must not see them.");
    }

    [TestMethod]
    public async Task An_unacknowledged_http_sign_in_is_refused()
    {
        await using var instance = await TestInstance.StartAsync();

        var response = await instance.Client.PostAsync(
            ApiRoutes.AdminSignIn,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = instance.InitialAdminPassword,
            }));

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual("/login?error=risk", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task The_bootstrap_credentials_unlock_the_ui_but_not_the_api()
    {
        await using var instance = await TestInstance.StartAsync();

        var signIn = await instance.SignInAsync("admin", instance.InitialAdminPassword);

        Assert.AreEqual(HttpStatusCode.Found, signIn.StatusCode);
        Assert.AreEqual(
            "/change-credentials",
            signIn.Headers.Location?.OriginalString,
            "§10.1: the first login must be forced straight into changing the credentials.");

        var blocked = await instance.Client.PostAsync(ApiRoutes.PairingCodes, content: null);
        var error = await blocked.Content.ReadFromJsonAsync<ApiError>();

        Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.AreEqual(ApiErrorCodes.CredentialsRequired, error?.Code);

        // Once the change is done, the same call succeeds — the gate is a state, not a permanent lock.
        await instance.CompleteForcedCredentialChangeAsync("owner", "CorrectHorseBattery1");
        await instance.SignInAsync("owner", "CorrectHorseBattery1");

        var allowed = await instance.Client.PostAsync(ApiRoutes.PairingCodes, content: null);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task Pairing_a_device_authenticates_it_and_revocation_takes_effect_immediately()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var issued = await instance.PostForJsonAsync<PairingCodeResponse>(ApiRoutes.PairingCodes);
        Assert.IsNotNull(issued);

        var redeemBody = new RedeemPairingCodeRequest(issued.Code, "Pixel 8", "android");

        using var redemption = await instance.Client.PostAsJsonAsync(ApiRoutes.PairingRedeem, redeemBody);
        var device = await redemption.Content.ReadFromJsonAsync<RedeemPairingCodeResponse>();

        Assert.AreEqual(HttpStatusCode.OK, redemption.StatusCode);
        Assert.IsNotNull(device);
        Assert.IsFalse(string.IsNullOrWhiteSpace(device.Token));

        using var deviceClient = new HttpClient { BaseAddress = instance.Client.BaseAddress };
        deviceClient.DefaultRequestHeaders.Authorization = new("Bearer", device.Token);

        var self = await deviceClient.GetAsync(ApiRoutes.DeviceSelf);
        Assert.AreEqual(HttpStatusCode.OK, self.StatusCode);

        // The admin's cookie must not work on a device-only endpoint, nor the device token on an admin one.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await instance.Client.GetAsync(ApiRoutes.DeviceSelf)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync(ApiRoutes.Devices)).StatusCode);

        // Replaying the code must not create a second device.
        using var replay = await instance.Client.PostAsJsonAsync(ApiRoutes.PairingRedeem, redeemBody);
        var replayError = await replay.Content.ReadFromJsonAsync<ApiError>();

        Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.AreEqual("pairing.code.already_used", replayError?.Code);

        var devices = await instance.GetJsonAsync<DeviceDto[]>(ApiRoutes.Devices);
        Assert.AreEqual(1, devices!.Length, "A replayed pairing code must not register a second device.");

        // Rotating invalidates the old token at once.
        var rotated = await instance.PostForJsonAsync<RotateDeviceTokenResponse>(
            $"{ApiRoutes.Devices}/{device.DeviceId}/rotate");
        Assert.IsNotNull(rotated);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync(ApiRoutes.DeviceSelf)).StatusCode);

        deviceClient.DefaultRequestHeaders.Authorization = new("Bearer", rotated.Token);
        Assert.AreEqual(HttpStatusCode.OK, (await deviceClient.GetAsync(ApiRoutes.DeviceSelf)).StatusCode);

        // Revoking stops the device on its very next request.
        var revoked = await instance.Client.DeleteAsync($"{ApiRoutes.Devices}/{device.DeviceId}");
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync(ApiRoutes.DeviceSelf)).StatusCode);
    }

    [TestMethod]
    public async Task Malformed_and_unknown_requests_answer_with_stable_codes()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var invalid = await instance.Client.PostAsJsonAsync(
            ApiRoutes.PairingRedeem,
            new RedeemPairingCodeRequest(string.Empty, string.Empty, null));
        var invalidError = await invalid.Content.ReadFromJsonAsync<ApiError>();

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(ApiErrorCodes.ValidationFailed, invalidError?.Code);

        using var unknown = await instance.Client.PostAsJsonAsync(
            ApiRoutes.PairingRedeem,
            new RedeemPairingCodeRequest("ZZZZ-ZZZZ", "Pixel 8", null));
        var unknownError = await unknown.Content.ReadFromJsonAsync<ApiError>();

        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual("pairing.code.unknown", unknownError?.Code);

        // A non-UUID path segment is a client mistake, not an unhandled exception.
        var badGuid = await instance.Client.DeleteAsync($"{ApiRoutes.Devices}/not-a-guid");
        Assert.AreEqual(HttpStatusCode.NotFound, badGuid.StatusCode);
    }

    [TestMethod]
    public async Task The_login_page_renders_for_an_anonymous_visitor()
    {
        await using var instance = await TestInstance.StartAsync();

        // Guarding a real regression: the API endpoints opt out of antiforgery, so an API-only suite stays green
        // even when the antiforgery middleware is missing from the pipeline — while every Blazor page 500s.
        var response = await instance.Client.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET /login failed:\n{html}");
        StringAssert.Contains(html, "action=\"/api/admin/sign-in\"", "The sign-in form must be rendered.");
        StringAssert.Contains(html, "把一天的碎片", "新的登录页应当显示产品说明，而不是只有一张裸表单。");
        StringAssert.Contains(html, "当前是 HTTP 连接", "HTTP 风险只需要在登录前集中提示一次。");
    }

    [TestMethod]
    public async Task An_anonymous_visit_to_the_dashboard_is_sent_to_the_login_page()
    {
        await using var instance = await TestInstance.StartAsync();

        var response = await instance.Client.GetAsync("/");

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        StringAssert.Contains(response.Headers.Location?.OriginalString ?? string.Empty, "/login");
    }

    [TestMethod]
    public async Task The_admin_pages_render_after_signing_in()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var dashboard = await instance.Client.GetAsync("/");
        var dashboardHtml = await dashboard.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, dashboard.StatusCode, $"GET / failed:\n{dashboardHtml}");

        // The page deliberately translates internal probe keys into user-facing labels.
        StringAssert.Contains(
            WebUtility.HtmlDecode(dashboardHtml),
            "数据库写入",
            "The health table must be on the status page without exposing internal probe names.");

        // Pair a device so the device page has something concrete to render.
        var issued = await instance.PostForJsonAsync<PairingCodeResponse>(ApiRoutes.PairingCodes);
        using var redemption = await instance.Client.PostAsJsonAsync(
            ApiRoutes.PairingRedeem,
            new RedeemPairingCodeRequest(issued!.Code, "Pixel 8", "android"));
        redemption.EnsureSuccessStatusCode();

        var devices = await instance.Client.GetAsync("/devices");
        var devicesHtml = await devices.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, devices.StatusCode, $"GET /devices failed:\n{devicesHtml}");
        StringAssert.Contains(devicesHtml, "Pixel 8", "The paired device must be listed.");
    }

    [TestMethod]
    public async Task A_restart_reuses_the_same_instance_state()
    {
        // §15.3 and §19: the schema is migrated at startup and the administrator is not recreated, which is what
        // "Docker Compose 环境可以从空目录部署" plus "restart keeps working" boils down to.
        var instance = await TestInstance.StartAsync();
        var root = instance.RootPath;
        var databasePath = instance.DatabasePath;

        try
        {
            Assert.IsTrue(File.Exists(databasePath), "The database must be created on first start.");

            await instance.SignInAsChangedAdministratorAsync();

            var beforeRestart = await instance.Client.GetAsync(ApiRoutes.Devices);
            Assert.AreEqual(HttpStatusCode.OK, beforeRestart.StatusCode);

            var cookie = instance.AdminCookieValue;
            Assert.IsFalse(string.IsNullOrWhiteSpace(cookie), "Signing in must issue an auth cookie.");

            // Assert the location, not just the behaviour. Without an explicit key-ring path ASP.NET Core falls
            // back to the user profile, which is persistent on a developer machine and ephemeral inside a
            // container — so "the session survived" alone would pass here even with the bug present.
            var keyRing = Path.Combine(root, "keys");
            Assert.IsTrue(
                Directory.Exists(keyRing) && Directory.EnumerateFiles(keyRing, "*.xml").Any(),
                $"The DataProtection key ring must be persisted under {keyRing}, outside the backup set.");

            // Stop without deleting: a restart means the process goes away, not the data.
            await instance.StopAsync();

            // Restarting against the same directory must not print a new password.
            var restarted = await TestInstance.StartAtAsync(root);
            await using var _ = restarted;

            Assert.IsFalse(
                restarted.BootstrapOutput.Contains("INITIAL-ADMIN-PASSWORD", StringComparison.Ordinal),
                "An existing instance must not generate a second administrator password.");

            var signIn = await restarted.SignInAsync("owner", "CorrectHorseBattery1");
            Assert.AreEqual(
                HttpStatusCode.Found,
                signIn.StatusCode,
                "The administrator changed on the previous run must still be able to sign in.");

            // The browser's existing session must survive too. That only holds if the DataProtection key ring
            // was persisted outside the container, which is exactly the regression this pins down.
            restarted.SetAdminCookie(cookie!);
            var afterRestart = await restarted.Client.GetAsync(ApiRoutes.Devices);

            Assert.AreEqual(
                HttpStatusCode.OK,
                afterRestart.StatusCode,
                "A restart must not invalidate existing administrator sessions.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // The stopped SQLite pool may still hold the file briefly; a leftover temp directory is fine.
            }
        }
    }
}
