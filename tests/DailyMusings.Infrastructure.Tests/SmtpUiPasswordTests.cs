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

        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets, secrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                Enabled: true,
                Host: "smtp.example.com",
                Port: 465,
                FromAddress: "noreply@example.com",
                UseSsl: true,
                UseStartTls: false,
                Password: "typed-in-the-page",
                ToAddress: "reader@example.com"),
            CancellationToken.None);

        // The value went to the encrypted store, under the one fixed name the whole product agrees on.
        Assert.AreEqual(
            "typed-in-the-page",
            await uiSecrets.GetAsync(SmtpSettingKeys.PasswordSecretName, CancellationToken.None));
        Assert.AreEqual(SecretSource.Ui, secrets.ResolveSource(SmtpSettingKeys.PasswordSecretName));
        Assert.AreEqual("typed-in-the-page", secrets.TryGet(SmtpSettingKeys.PasswordSecretName));

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.Ui, view.PasswordSource);
        Assert.IsTrue(view.HasPassword);
        Assert.IsTrue(view.Enabled);
        Assert.IsTrue(view.UseSsl, "The SSL checkbox reaches the reader.");
        Assert.IsFalse(view.UseStartTls);

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

    /// <summary>
    /// 「清除已保存的密码」要连 Secret 文件里的那份一起挡住。
    /// <para>
    /// 这条替换掉了原来「清除后回落到挂载的 Secret 文件」那条断言：那条在旧模型下说得通（密码只是被指向），
    /// 但新模型里「有密码就必须加密」，而部署里往往躺着一个从示例目录抄来的 <c>smtp-password</c>。若清除只删掉
    /// 页面那一份、解析继续落到文件，操作者会看到「请清空密码」的提示却怎么都清不掉，本来能发信的实例从此发不出去。
    /// 所以清除写下的是一条**显式的空记录**，它比文件优先；想重新用文件里的密码，就再填一次。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Clearing_the_password_also_overrides_the_mounted_secret_file()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var uiSecrets = new EncryptedUiSecretStore(paths);
        var secrets = new FileSecretStore(paths, uiSecrets);

        Directory.CreateDirectory(paths.SecretsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.SecretsPath, "smtp-password"), "from-the-file");

        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets, secrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                true,
                "smtp.example.com",
                587,
                "owner@example.com",
                UseSsl: false,
                UseStartTls: true,
                Password: "typed"),
            CancellationToken.None);

        Assert.AreEqual(SecretSource.Ui, secrets.ResolveSource(SmtpSettingKeys.PasswordSecretName));

        // Clearing it takes the file's password out of play too. The switch stays where it was, so the save that
        // clears the password is not also a save that turns encryption off.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, null, null, null, null, ClearPassword: true),
            CancellationToken.None);

        Assert.IsNull(secrets.TryGet(SmtpSettingKeys.PasswordSecretName), "清除之后文件里的密码不该再被取到");
        Assert.AreEqual(SecretSource.None, secrets.ResolveSource(SmtpSettingKeys.PasswordSecretName));

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.None, view.PasswordSource);
        Assert.IsFalse(view.HasPassword);
        Assert.IsTrue(view.UseStartTls, "Leaving the switches out of a save leaves them alone.");

        // And the configuration is now保存得下去 in the clear, which is the whole point of being able to clear it:
        // 没有密码的明文中继是合法的（本机中继），被挡住的是「有密码还不加密」。
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, null, null, UseSsl: false, UseStartTls: false),
            CancellationToken.None);
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
        var uiSecrets = new EncryptedUiSecretStore(paths);
        var secrets = new FileSecretStore(paths, uiSecrets);
        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets, secrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                true,
                "smtp.example.com",
                587,
                "a@example.com",
                UseSsl: false,
                UseStartTls: true),
            CancellationToken.None);

        var view = await ReadAsync(database, settings, secrets);

        Assert.AreEqual(SecretSource.None, view.PasswordSource);
        Assert.IsFalse(view.HasPassword);
    }

    /// <summary>
    /// No password and no encryption is a legal configuration — a relay on localhost — and the view has to be able to
    /// say "there is no password" without the page implying the setup is broken.
    /// </summary>
    [TestMethod]
    public async Task A_password_less_configuration_without_encryption_is_saved_as_it_is()
    {
        await using var database = await TestDatabase.CreateAsync();
        var paths = PathsFor(database);
        var settings = new SqliteAppSettingStore(database.Accessor, new TestClock(DateTimeOffset.UtcNow));
        var uiSecrets = new EncryptedUiSecretStore(paths);
        var secrets = new FileSecretStore(paths, uiSecrets);
        var update = new UpdateSmtpSettingsUseCase(settings, uiSecrets, secrets);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "127.0.0.1", 1025, "dailymusings@localhost", UseSsl: false, UseStartTls: false),
            CancellationToken.None);

        var view = await ReadAsync(database, settings, secrets);

        Assert.IsTrue(view.Enabled);
        Assert.IsFalse(view.UseSsl);
        Assert.IsFalse(view.UseStartTls);
        Assert.AreEqual(SecretSource.None, view.PasswordSource);
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
