using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Infrastructure.Generation;
using DailyMusings.Infrastructure.Notifications;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Transcription;
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
    public async Task Models_with_the_same_base_url_still_use_three_independent_api_keys()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);
        const string sharedBaseUrl = "https://compatible.example/v1";
        var configuration = Configuration(
            ("Transcription:BaseUrl", sharedBaseUrl),
            ("Generation:BaseUrl", sharedBaseUrl),
            ("Embedding:BaseUrl", sharedBaseUrl));

        var transcription = await new ConfigurationTranscriptionSettingsProvider(configuration, store)
            .GetAsync(CancellationToken.None);
        var generation = await new ConfigurationGenerationSettingsProvider(configuration, store)
            .GetAsync(CancellationToken.None);
        var embedding = await new ConfigurationEmbeddingSettingsProvider(configuration, store)
            .GetAsync(CancellationToken.None);

        Assert.AreEqual(sharedBaseUrl, transcription.BaseUrl);
        Assert.AreEqual(sharedBaseUrl, generation.BaseUrl);
        Assert.AreEqual(sharedBaseUrl, embedding.BaseUrl);
        Assert.AreEqual("openai-api-key", transcription.SecretName);
        Assert.AreEqual("deepseek-api-key", generation.SecretName);
        Assert.AreEqual("embedding-api-key", embedding.SecretName);
        CollectionAssert.AllItemsAreUnique(
            new[] { transcription.SecretName, generation.SecretName, embedding.SecretName },
            "Base URL 相同也不能让模型共用 API Key。每个模型必须解析自己的密钥槽位。");
    }

    [TestMethod]
    public async Task Transcription_provider_reads_the_explicit_saved_api_type()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        await new UpdateModelEndpointUseCase(store, new TestUiSecretStore()).ExecuteAsync(
            ModelService.Transcription,
            new ModelEndpointUpdate(
                null,
                null,
                null,
                null,
                null,
                null,
                ApiType: TranscriptionApiTypes.DashScopeAsync),
            CancellationToken.None);

        var settings = await new ConfigurationTranscriptionSettingsProvider(Configuration(), store)
            .GetAsync(CancellationToken.None);

        Assert.AreEqual(TranscriptionApiTypes.DashScopeAsync, settings.ApiType);
    }

    [TestMethod]
    public async Task What_the_admin_page_stored_wins_over_the_deployment_configuration()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        // Exactly what the admin page writes, through the use case that validates it.
        await new UpdateModelEndpointUseCase(store, new TestUiSecretStore()).ExecuteAsync(
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
        await new UpdateModelEndpointUseCase(store, new TestUiSecretStore()).ExecuteAsync(
            ModelService.Generation,
            new ModelEndpointUpdate(null, null, "changed-again", null, null, null),
            CancellationToken.None);

        Assert.AreEqual("changed-again", (await provider.GetAsync(CancellationToken.None)).Model);
    }

    [TestMethod]
    public async Task Smtp_reads_the_same_way_and_a_stored_value_wins_over_the_deployment()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);

        var provider = new ConfigurationSmtpSettingsProvider(
            Configuration(("Smtp:Enabled", "false"), ("Smtp:Host", "compose-smtp"), ("Smtp:Port", "2525")),
            store);

        var fromCompose = await provider.GetAsync(CancellationToken.None);
        Assert.AreEqual("compose-smtp", fromCompose.Host);
        Assert.AreEqual(2525, fromCompose.Port);
        Assert.IsFalse(fromCompose.Enabled);

        var update = new UpdateSmtpSettingsUseCase(store, new TestUiSecretStore(), TestSecretStore.Empty());
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "admin-smtp", 465, "owner@example.com", UseSsl: true, UseStartTls: false),
            CancellationToken.None);

        var fromAdmin = await provider.GetAsync(CancellationToken.None);
        Assert.AreEqual("admin-smtp", fromAdmin.Host);
        Assert.AreEqual(465, fromAdmin.Port);
        Assert.IsTrue(fromAdmin.Enabled);
        Assert.AreEqual("owner@example.com", fromAdmin.FromAddress, "The sender's mailbox is the SMTP username.");
        Assert.IsTrue(fromAdmin.UseSsl);
        Assert.IsFalse(fromAdmin.UseStartTls, "The pair is written together: choosing SSL leaves STARTTLS off.");

        // And the very next read sees it: no restart, which is what an admin editor is for.
        Assert.AreEqual("admin-smtp", (await provider.GetAsync(CancellationToken.None)).Host);
    }

    /// <summary>
    /// The two switches replaced one "security" token, which had itself replaced a boolean, and neither old spelling
    /// may be silently ignored: an instance that saved "use STARTTLS" must not stop sending, and a compose file that
    /// sets <c>Smtp__UseStartTls</c> or <c>Smtp__Security</c> must keep working. The new keys win where both are
    /// present, because that is what the admin page writes.
    /// </summary>
    [TestMethod]
    public async Task Smtp_reads_the_two_switches_and_still_honours_the_old_keys()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = Store(database);
        var update = new UpdateSmtpSettingsUseCase(store, new TestUiSecretStore(), TestSecretStore.Empty());

        // A deployment configured only from compose, in the oldest spelling.
        var legacyCompose = new ConfigurationSmtpSettingsProvider(Configuration(("Smtp:UseStartTls", "true")), store);
        var fromLegacyCompose = await legacyCompose.GetAsync(CancellationToken.None);

        Assert.IsTrue(fromLegacyCompose.UseStartTls, "Smtp__UseStartTls=true still means STARTTLS.");
        Assert.IsFalse(fromLegacyCompose.UseSsl);

        // The token that came between them, which could also say "implicit TLS on 465".
        var tokenCompose = new ConfigurationSmtpSettingsProvider(Configuration(("Smtp:Security", "ssl")), store);
        var fromTokenCompose = await tokenCompose.GetAsync(CancellationToken.None);

        Assert.IsTrue(fromTokenCompose.UseSsl, "Smtp__Security=ssl still means SSL.");
        Assert.IsFalse(fromTokenCompose.UseStartTls);

        var noneCompose = new ConfigurationSmtpSettingsProvider(Configuration(("Smtp:Security", "none")), store);
        var fromNoneCompose = await noneCompose.GetAsync(CancellationToken.None);

        Assert.IsFalse(fromNoneCompose.UseSsl);
        Assert.IsFalse(fromNoneCompose.UseStartTls, "Smtp__Security=none still means neither.");

        // The new spelling the form writes, and it is authoritative even when it says false.
        var newCompose = new ConfigurationSmtpSettingsProvider(
            Configuration(("Smtp:Ssl", "true"), ("Smtp:StartTls", "false"), ("Smtp:Security", "starttls"), ("Smtp:UseStartTls", "true")),
            store);
        var fromNewCompose = await newCompose.GetAsync(CancellationToken.None);

        Assert.IsTrue(fromNewCompose.UseSsl);
        Assert.IsFalse(fromNewCompose.UseStartTls, "The new key wins over both old spellings.");

        // An unreadable value falls through to the next source instead of stopping the notification job.
        var typo = new ConfigurationSmtpSettingsProvider(
            Configuration(("Smtp:Ssl", "not-a-boolean"), ("Smtp:Security", "yes-please"), ("Smtp:StartTls", "also-not")),
            store);
        var fromTypo = await typo.GetAsync(CancellationToken.None);

        Assert.IsFalse(fromTypo.UseSsl);
        Assert.IsFalse(fromTypo.UseStartTls);

        // The admin page wins over both, in whichever spelling the deployment still has.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, 465, null, UseSsl: true, UseStartTls: false),
            CancellationToken.None);

        var fromAdmin = await new ConfigurationSmtpSettingsProvider(
            Configuration(("Smtp:Security", "starttls")),
            store).GetAsync(CancellationToken.None);

        Assert.IsTrue(fromAdmin.UseSsl, "The stored pair wins over the deployment's old token.");
        Assert.IsFalse(fromAdmin.UseStartTls);

        // And an instance whose settings table only ever had one of the old keys keeps working.
        await using var legacyDatabase = await TestDatabase.CreateAsync();
        var legacyStore = Store(legacyDatabase);
        await legacyStore.SetAsync("smtp.security", "starttls", CancellationToken.None);

        var fromLegacyStore = await new ConfigurationSmtpSettingsProvider(Configuration(), legacyStore)
            .GetAsync(CancellationToken.None);

        Assert.IsTrue(fromLegacyStore.UseStartTls, "A stored smtp.security=starttls still means STARTTLS.");
        Assert.IsFalse(fromLegacyStore.UseSsl);

        await using var booleanDatabase = await TestDatabase.CreateAsync();
        var booleanStore = Store(booleanDatabase);
        await booleanStore.SetAsync("smtp.useStartTls", "true", CancellationToken.None);

        var fromBooleanStore = await new ConfigurationSmtpSettingsProvider(Configuration(), booleanStore)
            .GetAsync(CancellationToken.None);

        Assert.IsTrue(fromBooleanStore.UseStartTls, "The boolean that predates the token is still honoured.");
        Assert.IsFalse(fromBooleanStore.UseSsl);
    }
}
