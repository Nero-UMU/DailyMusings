using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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

    public static Task<TestInstance> StartAsync() =>
        StartAtAsync(Path.Combine(Path.GetTempPath(), "dailymusings-api", Guid.CreateVersion7().ToString("N")));

    /// <summary>Starts against a specific instance directory, so a test can simulate a restart.</summary>
    public static async Task<TestInstance> StartAtAsync(string rootPath)
    {
        Directory.CreateDirectory(rootPath);

        var root = rootPath;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = root,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = root,
            ["Storage:SecretsPath"] = Path.Combine(root, "secrets"),

            // Kept beside the instance root, mirroring production: the key ring must outlive a restart but stay
            // out of the backup set.
            ["Storage:KeyRingPath"] = Path.Combine(root, "keys"),
        });

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
