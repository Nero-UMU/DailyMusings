using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Infrastructure.Configuration;
using DailyMusings.Infrastructure.Notifications;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The SMTP password an operator can now type into the admin page (docs/开发指导.md §10.4 as revised, §12).
/// <para>
/// What is being pinned down here is the <em>lookup order</em> and the honest reporting of it: the admin-page
/// value wins, a mounted secret file is second, the environment third — and the page can tell the difference
/// between "you have not set a password" and "yours is in a file I can read".
/// </para>
/// </summary>
[TestClass]
public class SmtpUiPasswordTests
{
    private const string EnvironmentSecretName = "smtp-test-env-only";

    [TestMethod]
    public async Task A_password_typed_into_the_page_is_used_and_reported_as_the_ui_source()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var uiSecrets = new EncryptedUiSecretStore(paths);
        var secrets = new FileSecretStore(paths, uiSecrets);

        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                Enabled: true,
                Host: "smtp.example.com",
                Port: 465,
                Security: SmtpSecurity.ImplicitTls,
                Username: "owner@example.com",
                SecretName: "smtp-password",
                FromAddress: "noreply@example.com",
                FromName: "每日随想",
                TimeoutSeconds: 30,
                Password: "typed-in-the-page",
                ToAddress: "reader@example.com"),
            CancellationToken.None);

        // The value went to the encrypted store, under the name the settings row points at.
        Assert.AreEqual("typed-in-the-page", await uiSecrets.GetAsync("smtp-password", CancellationToken.None));
        Assert.AreEqual(SecretSource.Ui, secrets.ResolveSource("smtp-password"));
        Assert.AreEqual("typed-in-the-page", secrets.TryGet("smtp-password"));

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.Ui, view.PasswordSource);
        Assert.IsTrue(view.HasPassword);
        Assert.IsTrue(view.Ready, "Switched on with a resolvable password is what 'it will send' means.");

        // §12: the recipient is edited next to the server because it is the same errand, and it is stored under
        // the key the notifier already reads.
        Assert.AreEqual("reader@example.com", view.ToAddress);
        Assert.AreEqual("reader@example.com", await settings.GetAsync(NotificationSettingKeys.ToAddress, CancellationToken.None));

        // The password itself is never part of the view.
        Assert.IsFalse(
            view.GetType().GetProperties().Any(property =>
                property.GetValue(view) is string text && text.Contains("typed-in-the-page", StringComparison.Ordinal)),
            "No field of the view may carry the password (§10.4).");
    }

    [TestMethod]
    public async Task Clearing_the_password_falls_back_to_the_mounted_secret_file()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var uiSecrets = new EncryptedUiSecretStore(paths);
        var secrets = new FileSecretStore(paths, uiSecrets);

        Directory.CreateDirectory(paths.SecretsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.SecretsPath, "smtp-password"), "from-the-file");

        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "smtp.example.com", 587, SmtpSecurity.StartTls, "owner", "smtp-password", "a@example.com", "每日随想", 30, Password: "typed"),
            CancellationToken.None);

        Assert.AreEqual(SecretSource.Ui, secrets.ResolveSource("smtp-password"));

        await update.ExecuteAsync(new SmtpSettingsUpdate(null, null, null, null, null, null, null, null, null, ClearPassword: true), CancellationToken.None);

        Assert.IsNull(await uiSecrets.GetAsync("smtp-password", CancellationToken.None));
        Assert.AreEqual(SecretSource.File, secrets.ResolveSource("smtp-password"));
        Assert.AreEqual("from-the-file", secrets.TryGet("smtp-password"));

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.File, view.PasswordSource);
        Assert.IsTrue(view.HasPassword);
    }

    /// <summary>
    /// Neither a page value nor a file: the view says <c>none</c> rather than claiming a password exists, which
    /// is what lets the admin page tell the operator exactly what is missing (§12).
    /// </summary>
    [TestMethod]
    public async Task With_no_password_anywhere_the_view_says_none()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var secrets = new FileSecretStore(paths, new EncryptedUiSecretStore(paths));

        var update = new UpdateSmtpSettingsUseCase(settings, new EncryptedUiSecretStore(paths));

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "smtp.example.com", 587, SmtpSecurity.StartTls, null, "smtp-password", "a@example.com", "每日随想", 30),
            CancellationToken.None);

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.None, view.PasswordSource);
        Assert.IsFalse(view.HasPassword);
        Assert.IsFalse(view.Ready, "Enabled without a password is not ready, and the page has to be able to say so.");
    }

    [TestMethod]
    public async Task The_environment_is_the_last_resort()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var secrets = new FileSecretStore(paths, new EncryptedUiSecretStore(paths));

        // The prefix the store reads, in the spelling it derives from a secret name.
        var variable = "DAILYMUSINGS_SECRET_" + EnvironmentSecretName.Replace('-', '_').ToUpperInvariant();

        try
        {
            Environment.SetEnvironmentVariable(variable, "from-the-environment");

            Assert.AreEqual(SecretSource.Environment, secrets.ResolveSource(EnvironmentSecretName));
            Assert.AreEqual("from-the-environment", secrets.TryGet(EnvironmentSecretName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.AreEqual(SecretSource.None, secrets.ResolveSource(EnvironmentSecretName));

        await Task.CompletedTask;
        _ = settings;
    }

    private static InstancePaths PathsFor(TestDatabase database)
    {
        var paths = new InstancePaths(new StorageOptions
        {
            RootPath = database.RootPath,
            SecretsPath = Path.Combine(database.RootPath, "secrets"),
            KeyRingPath = Path.Combine(database.RootPath, "keys"),
        });

        paths.EnsureCreated();
        return paths;
    }

    private static Task<SmtpSettingsView> ReadAsync(
        TestDatabase database,
        SqliteAppSettingStore settings,
        ISecretStore secrets)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        return new ReadSmtpSettingsUseCase(
                new ConfigurationSmtpSettingsProvider(configuration, settings),
                secrets,
                new ConfigurationNotificationSettingsProvider(configuration, settings))
            .ExecuteAsync(CancellationToken.None);
    }
}
