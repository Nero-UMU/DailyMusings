using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DailyMusings.Contracts;
using DailyMusings.Server.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// Runs the real application in-process against a throwaway instance directory.
/// <para>
/// It composes the same registrations and the same pipeline the host uses
/// (<c>AddDailyMusingsServer</c> + <c>InitializeDailyMusingsAsync</c> + <c>UseDailyMusings</c>) and then talks
/// to it over a real socket. A test that assembled its own pipeline would only prove the test's pipeline works.
/// </para>
/// </summary>
internal sealed class TestInstance : IAsyncDisposable
{
    private static readonly Regex PasswordPattern = new(
        @"^INITIAL-ADMIN-PASSWORD=(?<password>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly WebApplication _app;
    private readonly CookieContainer _cookies;

    private TestInstance(WebApplication app, HttpClient client, CookieContainer cookies, string rootPath, string bootstrapOutput)
    {
        _app = app;
        Client = client;
        _cookies = cookies;
        RootPath = rootPath;
        BootstrapOutput = bootstrapOutput;
    }

    public HttpClient Client { get; }

    public string RootPath { get; }

    /// <summary>Everything the bootstrap wrote to the terminal. Never contains anything else about the account.</summary>
    public string BootstrapOutput { get; }

    public string InitialAdminPassword
    {
        get
        {
            var match = PasswordPattern.Match(BootstrapOutput);

            Assert.IsTrue(match.Success, $"No INITIAL-ADMIN-PASSWORD line was printed. Output was:\n{BootstrapOutput}");
            return match.Groups["password"].Value;
        }
    }

    public static Task<TestInstance> StartAsync(
        IReadOnlyDictionary<string, string?>? extraSettings = null,
        string environmentName = "Production") =>
        StartAtAsync(
            Path.Combine(Path.GetTempPath(), "dailymusings-api", Guid.CreateVersion7().ToString("N")),
            extraSettings,
            environmentName);

    /// <summary>Starts against a specific instance directory, so a test can simulate a restart.</summary>
    public static async Task<TestInstance> StartAtAsync(
        string rootPath,
        IReadOnlyDictionary<string, string?>? extraSettings = null,
        string environmentName = "Production")
    {
        Directory.CreateDirectory(rootPath);

        var root = rootPath;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ContentRootPath = root,
        });

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Storage:RootPath"] = root,

            // Kept beside the instance root, mirroring production: the key ring must outlive a restart but stay
            // out of the backup set.
            ["Storage:KeyRingPath"] = Path.Combine(root, "keys"),
        };

        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                settings[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(settings);

        // Keep the test output readable; failures are asserted, not read from logs.
        builder.Logging.ClearProviders();

        // Port 0 lets the OS pick a free port, so tests never collide with a running instance.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDailyMusingsServer(builder.Configuration);
        builder.Services.AddHttpContextAccessor();

        var app = builder.Build();

        // The bootstrap banner goes to the terminal by design (§10.1). Capturing it here is how the test learns
        // the generated password — which also proves it is not written anywhere else.
        var originalOut = Console.Out;
        var captured = new StringWriter();

        try
        {
            Console.SetOut(captured);
            await app.InitializeDailyMusingsAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        app.UseDailyMusings();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var baseAddress = addresses!.Addresses.First();

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),

            // Redirects are assertions here: "302 to /change-credentials" is the behaviour under test.
            AllowAutoRedirect = false,
        };

        return new TestInstance(
            app,
            new HttpClient(handler) { BaseAddress = new Uri(baseAddress) },
            handler.CookieContainer,
            root,
            captured.ToString());
    }

    /// <summary>
    /// The administrator's auth cookie, as a browser would still be holding it. Used to assert that a restart
    /// does not invalidate existing sessions, which depends on the DataProtection key ring being persisted.
    /// </summary>
    public string? AdminCookieValue =>
        _cookies.GetCookies(Client.BaseAddress!)["dailymusings.admin"]?.Value;

    /// <summary>Plants a previously issued auth cookie, simulating a browser that kept its session.</summary>
    public void SetAdminCookie(string value) =>
        _cookies.Add(Client.BaseAddress!, new Cookie("dailymusings.admin", value, "/"));

    /// <summary>
    /// Provisions a secret the way an operator does: through the admin page's encrypted store (appendix A.27).
    /// Before that decision this helper dropped a file into a mounted secrets directory; a deployment can no
    /// longer provide a credential at all, so a test that needs one has to use the same door.
    /// </summary>
    public void WriteSecret(string name, string value) =>
        _app.Services
            .GetRequiredService<DailyMusings.Application.Abstractions.IUiSecretStore>()
            .SetAsync(name, value, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Pairs a device through the real pairing flow and returns an HTTP client authenticated as that device —
    /// which is exactly how a phone will talk to this server, rather than an admin shortcut.
    /// </summary>
    public async Task<(string DeviceId, HttpClient Client)> PairDeviceAsync(string deviceName = "Pixel 8")
    {
        var issued = await PostForJsonAsync<PairingCodeResponse>(ApiRoutes.PairingCodes);

        using var redemption = await Client.PostAsJsonAsync(
            ApiRoutes.PairingRedeem,
            new RedeemPairingCodeRequest(issued!.Code, deviceName, "android"));

        redemption.EnsureSuccessStatusCode();

        var device = await redemption.Content.ReadFromJsonAsync<RedeemPairingCodeResponse>();

        var client = new HttpClient { BaseAddress = Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new("Bearer", device!.Token);

        return (device.DeviceId, client);
    }

    /// <summary>Signs in through the real form endpoint, acknowledging the plain-HTTP risk notice.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string username, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["acknowledgeRisk"] = "yes",
        };

        return await Client
            .PostAsync("/api/admin/sign-in", new FormUrlEncodedContent(form))
            .ConfigureAwait(false);
    }

    /// <summary>Replaces the bootstrap credentials, which §10.1 forces before anything else works.</summary>
    public async Task CompleteForcedCredentialChangeAsync(string newUsername, string newPassword)
    {
        var form = new Dictionary<string, string>
        {
            ["currentPassword"] = InitialAdminPassword,
            ["newUsername"] = newUsername,
            ["newPassword"] = newPassword,
            ["confirmPassword"] = newPassword,
        };

        var response = await Client
            .PostAsync("/api/admin/credentials", new FormUrlEncodedContent(form))
            .ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
    }

    /// <summary>Signs in as the changed administrator, leaving the client holding a usable cookie.</summary>
    public async Task SignInAsChangedAdministratorAsync(
        string username = "owner",
        string password = "CorrectHorseBattery1")
    {
        // The forced change is only permitted for an authenticated administrator, so sign in as the bootstrap
        // account first — which is exactly the sequence a real operator follows.
        var bootstrapSignIn = await SignInAsync(DailyMusings.Domain.Identity.AdminAccount.InitialUsername, InitialAdminPassword);

        Assert.AreEqual(HttpStatusCode.Found, bootstrapSignIn.StatusCode);

        await CompleteForcedCredentialChangeAsync(username, password);

        var response = await SignInAsync(username, password);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual("/", response.Headers.Location?.OriginalString);
    }

    public async Task<T?> GetJsonAsync<T>(string path)
    {
        var response = await Client.GetAsync(path).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
    }

    /// <summary>
    /// The instance's own content day, as <c>yyyy-MM-dd</c>.
    /// <para>
    /// Tests must not derive "today" from UTC. The content time zone is <c>Asia/Shanghai</c> by default, so between
    /// 16:00 and 24:00 UTC the instance is already on the next day and a UTC-derived date is correctly refused with
    /// <c>reflection.regeneration.date_not_current</c> — which made a test fail every night in that window. Asking
    /// the instance is one request and is true at every hour.
    /// </para>
    /// </summary>
    public async Task<string> ContentDateAsync()
    {
        var statistics = await GetJsonAsync<StatisticsResponse>("/api/system/statistics").ConfigureAwait(false);
        return statistics!.Today;
    }

    /// <summary>For endpoints that create something and take no body, e.g. issuing a pairing code.</summary>
    public async Task<T?> PostForJsonAsync<T>(string path)
    {
        var response = await Client.PostAsync(path, content: null).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
    }

    private volatile bool _stopped;

    /// <summary>
    /// Stops the application and releases its HTTP client, leaving the instance directory untouched.
    /// <para>
    /// Separate from <see cref="DisposeAsync"/> on purpose: a test that simulates a restart must stop the process
    /// while keeping the state, and conflating the two made an earlier version of that test delete the very key
    /// ring it was trying to prove had been persisted.
    /// </para>
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        Client.Dispose();

        try
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
