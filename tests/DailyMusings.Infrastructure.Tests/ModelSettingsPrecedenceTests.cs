using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Infrastructure.Generation;
using DailyMusings.Infrastructure.Notifications;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// Where the model and SMTP settings come from, and in what order (docs/开发指导.md §8.1, §12).
/// <para>
/// Two audiences have to keep working: an instance configured entirely from compose (nothing stored, so every read
/// falls through to the environment) and an instance configured from the admin page (stored wins, and the change is
/// picked up by the next call rather than the next restart). Both are asserted here because the second one is the
/// whole point of the editor and the first one is how existing deployments stay working.
/// </para>
/// </summary>
[TestClass]
public class ModelSettingsPrecedenceTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 19, 0, 0, TimeSpan.Zero);

    private static TestClock Clock() => new(Now);

    private static SqliteAppSettingStore Store(TestDatabase database) => new(database.Accessor, Clock());

    [TestMethod]
    public async Task With_nothing_stored_the_deployment_configuration_is_used()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        var provider = new ConfigurationGenerationSettingsProvider(
            Configuration(
                ("Generation:Enabled", "true"),
                ("Generation:BaseUrl", "https://from-compose.example/v1"),
                ("Generation:Model", "compose-model"),
                ("Generation:TimeoutSeconds", "33")),
            store);

        var settings = await provider.GetAsync(CancellationToken.None);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual("https://from-compose.example/v1", settings.BaseUrl);
        Assert.AreEqual("compose-model", settings.Model);
        Assert.AreEqual(TimeSpan.FromSeconds(33), settings.Timeout);
    }

    [TestMethod]
    public async Task With_nothing_stored_and_nothing_configured_the_built_in_default_applies()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        var settings = await new ConfigurationEmbeddingSettingsProvider(Configuration(), store)
            .GetAsync(CancellationToken.None);

        Assert.IsFalse(settings.Enabled, "A fresh instance sends nothing anywhere until an operator turns it on.");
        Assert.AreEqual(EmbeddingSettings.Default.Model, settings.Model);
    }

    [TestMethod]
    public async Task What_the_admin_page_stored_wins_over_the_deployment_configuration()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        // Exactly what the admin page writes, through the use case that validates it.
        await new UpdateModelEndpointUseCase(store).ExecuteAsync(
            ModelService.Generation,
            new ModelEndpointUpdate(true, "https://from-admin.example/v1", "admin-model", "admin-secret", 90, null),
            CancellationToken.None);

        var provider = new ConfigurationGenerationSettingsProvider(
            Configuration(
                ("Generation:Enabled", "false"),
                ("Generation:BaseUrl", "https://from-compose.example/v1"),
                ("Generation:Model", "compose-model")),
            store);

        var settings = await provider.GetAsync(CancellationToken.None);

        Assert.IsTrue(settings.Enabled, "The stored switch wins.");
        Assert.AreEqual("https://from-admin.example/v1", settings.BaseUrl);
        Assert.AreEqual("admin-model", settings.Model);
        Assert.AreEqual("admin-secret", settings.SecretName, "Only the name travels through the settings table (§10.4).");
        Assert.AreEqual(TimeSpan.FromSeconds(90), settings.Timeout);

        // And the very next read sees it: no restart, which is what an admin editor is for.
        await new UpdateModelEndpointUseCase(store).ExecuteAsync(
            ModelService.Generation,
            new ModelEndpointUpdate(null, null, "changed-again", null, null, null),
            CancellationToken.None);

        Assert.AreEqual("changed-again", (await provider.GetAsync(CancellationToken.None)).Model);
    }

    [TestMethod]
    public async Task Smtp_reads_the_same_way_and_a_cleared_username_stays_cleared()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        var provider = new ConfigurationSmtpSettingsProvider(
            Configuration(("Smtp:Enabled", "false"), ("Smtp:Host", "compose-smtp"), ("Smtp:Username", "compose-user")),
            store);

        var fromCompose = await provider.GetAsync(CancellationToken.None);
        Assert.AreEqual("compose-smtp", fromCompose.Host);
        Assert.AreEqual("compose-user", fromCompose.Username);

        var update = new UpdateSmtpSettingsUseCase(store);
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "admin-smtp", 2525, null, string.Empty, null, null, null, null),
            CancellationToken.None);

        var fromAdmin = await provider.GetAsync(CancellationToken.None);
        Assert.AreEqual("admin-smtp", fromAdmin.Host);
        Assert.AreEqual(2525, fromAdmin.Port);
        Assert.IsTrue(fromAdmin.Enabled);
        Assert.IsNull(fromAdmin.Username, "An empty stored username means 'none', not 'fall back to the environment'.");
    }
}
