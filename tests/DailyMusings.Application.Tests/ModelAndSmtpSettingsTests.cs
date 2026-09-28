using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>The settings store these tests write to: a dictionary with the same contract as the SQLite one.</summary>
internal sealed class InMemoryAppSettingStore : IAppSettingStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_values, StringComparer.Ordinal));

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, string> Snapshot() => _values;
}

/// <summary>
/// §8.1's admin-side configuration of the three model endpoints and §12's SMTP server: what may be written, what is
/// refused, and the rule that only a secret's <em>name</em> ever passes through here (§10.4).
/// </summary>
[TestClass]
public class ModelAndSmtpSettingsTests
{
    private static (UpdateModelEndpointUseCase Update, InMemoryAppSettingStore Store) Model() =>
        (new UpdateModelEndpointUseCase(new InMemoryAppSettingStore(), new InMemoryUiSecretStore()), new InMemoryAppSettingStore());

    [TestMethod]
    public async Task Saving_an_endpoint_writes_every_field_the_guide_lists()
    {
        var store = new InMemoryAppSettingStore();

        await new UpdateModelEndpointUseCase(store, new InMemoryUiSecretStore()).ExecuteAsync(
            ModelService.Embedding,
            new ModelEndpointUpdate(
                Enabled: true,
                BaseUrl: "https://api.example.com/v1/",
                Model: "text-embedding-3-small",
                SecretName: "embedding-api-key",
                TimeoutSeconds: 45,
                Dimensions: 512),
            CancellationToken.None);

        var written = store.Snapshot();

        Assert.AreEqual("true", written[ModelSettingKeys.Enabled(ModelService.Embedding)]);
        Assert.AreEqual("https://api.example.com/v1", written[ModelSettingKeys.BaseUrl(ModelService.Embedding)], "A trailing slash is stripped: clients append their own path.");
        Assert.AreEqual("text-embedding-3-small", written[ModelSettingKeys.Model(ModelService.Embedding)]);
        Assert.AreEqual("embedding-api-key", written[ModelSettingKeys.SecretName(ModelService.Embedding)]);
        Assert.AreEqual("45", written[ModelSettingKeys.TimeoutSeconds(ModelService.Embedding)]);
        Assert.AreEqual("512", written[ModelSettingKeys.Dimensions(ModelService.Embedding)]);
    }

    [TestMethod]
    public async Task A_field_left_out_is_a_field_left_alone()
    {
        var store = new InMemoryAppSettingStore();
        var update = new UpdateModelEndpointUseCase(store, new InMemoryUiSecretStore());

        await update.ExecuteAsync(
            ModelService.Generation,
            new ModelEndpointUpdate(true, "https://api.example.com/v1", "gpt-4o-mini", null, null, null),
            CancellationToken.None);

        var before = store.Snapshot();
        Assert.IsFalse(before.ContainsKey(ModelSettingKeys.TimeoutSeconds(ModelService.Generation)));

        await update.ExecuteAsync(
            ModelService.Generation,
            new ModelEndpointUpdate(null, null, "gpt-4.1-mini", null, null, null),
            CancellationToken.None);

        var after = store.Snapshot();
        Assert.AreEqual("gpt-4.1-mini", after[ModelSettingKeys.Model(ModelService.Generation)]);
        Assert.AreEqual("true", after[ModelSettingKeys.Enabled(ModelService.Generation)], "The switch keeps the value the first call wrote.");
        Assert.AreEqual("https://api.example.com/v1", after[ModelSettingKeys.BaseUrl(ModelService.Generation)]);
    }

    [TestMethod]
    public async Task Values_that_would_fail_at_three_in_the_morning_are_refused_here()
    {
        var update = new UpdateModelEndpointUseCase(new InMemoryAppSettingStore(), new InMemoryUiSecretStore());

        foreach (var (update_, code) in new (ModelEndpointUpdate, string)[]
                 {
                     (new ModelEndpointUpdate(null, "not-a-url", null, null, null, null), "model.baseUrl.invalid"),
                     (new ModelEndpointUpdate(null, "ftp://example.com/v1", null, null, null, null), "model.baseUrl.invalid"),
                     (new ModelEndpointUpdate(null, null, "  ", null, null, null), "model.name.invalid"),
                     (new ModelEndpointUpdate(null, null, null, "../../etc/passwd", null, null), "model.secret_name.invalid"),
                     (new ModelEndpointUpdate(null, null, null, null, 0, null), "model.timeout.out_of_range"),
                     (new ModelEndpointUpdate(null, null, null, null, 100_000, null), "model.timeout.out_of_range"),
                 })
        {
            var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
                update.ExecuteAsync(ModelService.Generation, update_, CancellationToken.None));

            Assert.AreEqual(code, failure.Code);
        }

        var dimensions = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            update.ExecuteAsync(
                ModelService.Transcription,
                new ModelEndpointUpdate(null, null, null, null, null, 512),
                CancellationToken.None));

        Assert.AreEqual("model.dimensions.not_applicable", dimensions.Code, "Only the embedding endpoint has a dimension.");
    }

    [TestMethod]
    public async Task A_secret_value_is_never_stored_only_its_name()
    {
        var store = new InMemoryAppSettingStore();

        await new UpdateModelEndpointUseCase(store, new InMemoryUiSecretStore()).ExecuteAsync(
            ModelService.Transcription,
            new ModelEndpointUpdate(true, "https://api.example.com/v1", "whisper-1", "openai-api-key", 120, null),
            CancellationToken.None);

        // The name travels; the value stays in the secret store (§10.4), which is the whole reason this form can be
        // served at all.
        CollectionAssert.AreEquivalent(
            new[] { "model.transcription.enabled", "model.transcription.baseUrl", "model.transcription.model", "model.transcription.secretName", "model.transcription.timeoutSeconds" },
            store.Snapshot().Keys.ToArray());
    }

    [TestMethod]
    public async Task A_public_url_only_ASR_model_is_refused_before_it_breaks_phone_recordings()
    {
        var update = new UpdateModelEndpointUseCase(new InMemoryAppSettingStore(), new InMemoryUiSecretStore());

        var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            update.ExecuteAsync(
                ModelService.Transcription,
                new ModelEndpointUpdate(null, null, "paraformer-v2", null, null, null),
                CancellationToken.None));

        Assert.AreEqual("model.transcription.public_url_only", failure.Code);
    }

    [TestMethod]
    public async Task Smtp_settings_are_written_and_validated_the_same_way()
    {
        var store = new InMemoryAppSettingStore();
        var update = new UpdateSmtpSettingsUseCase(store, new InMemoryUiSecretStore(), InMemorySecretStore.Empty());

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                Enabled: true,
                Host: "smtp.example.com",
                Port: 587,
                FromAddress: "dailymusings@example.com",
                UseSsl: false,
                UseStartTls: true),
            CancellationToken.None);

        var written = store.Snapshot();

        Assert.AreEqual("smtp.example.com", written[SmtpSettingKeys.Host]);
        Assert.AreEqual("587", written[SmtpSettingKeys.Port]);
        Assert.AreEqual("dailymusings@example.com", written[SmtpSettingKeys.FromAddress]);
        Assert.AreEqual("true", written[SmtpSettingKeys.StartTls]);
        Assert.AreEqual("false", written[SmtpSettingKeys.Ssl], "The pair is written together, so the other half is explicit.");
        Assert.AreEqual("true", written[SmtpSettingKeys.Enabled]);

        // And 465 is expressible, which is the reason the setting stopped being one token: this is the
        // "SSL: true, STARTTLS: false" that every other mail form shows.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, 465, null, UseSsl: true, UseStartTls: false),
            CancellationToken.None);

        Assert.AreEqual("true", store.Snapshot()[SmtpSettingKeys.Ssl]);
        Assert.AreEqual("false", store.Snapshot()[SmtpSettingKeys.StartTls]);

        // A recipient is optional — an empty one clears it, which is a legal configuration that sends nothing.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, null, null, null, null, ToAddress: "reader@example.com"),
            CancellationToken.None);

        Assert.AreEqual("reader@example.com", store.Snapshot()[NotificationSettingKeys.ToAddress]);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, null, null, null, null, ToAddress: "   "),
            CancellationToken.None);

        Assert.AreEqual(string.Empty, store.Snapshot()[NotificationSettingKeys.ToAddress]);

        foreach (var (change, code) in new (SmtpSettingsUpdate, string)[]
                 {
                     (new SmtpSettingsUpdate(null, "bad host", null, null, null, null), "smtp.host.invalid"),
                     (new SmtpSettingsUpdate(null, null, 0, null, null, null), "smtp.port.out_of_range"),
                     (new SmtpSettingsUpdate(null, null, 70_000, null, null, null), "smtp.port.out_of_range"),
                     (new SmtpSettingsUpdate(null, null, null, "not-an-address", null, null), "smtp.from_address.invalid"),
                     (new SmtpSettingsUpdate(null, null, null, null, null, null, ToAddress: "not-an-address"), "smtp.to_address.invalid"),
                 })
        {
            var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
                update.ExecuteAsync(change, CancellationToken.None));

            Assert.AreEqual(code, failure.Code);
        }
    }

    /// <summary>
    /// 发件人显示名必须是一个收件人读得懂的名字，否则回落到默认值。
    /// <para>
    /// 这条来自真实事故：实例的设置表里躺着 <c>smtp.fromName = "????"</c>（早期配置脚本把中文按 ANSI 发出去，
    /// 编不出来的字符全变成问号），于是收件人看到的是 <c>From: ???? &lt;…&gt;</c>。报头编码器本身没问题
    /// ——主题里的中文编码得好好的——所以唯一能防住这类脏值的地方，就是别把它放进报头。
    /// </para>
    /// </summary>
    [TestMethod]
    public void The_sender_display_name_falls_back_when_it_carries_nothing_readable()
    {
        Assert.AreEqual("每日随想", SmtpSettings.UsableFromName("每日随想"), "正常的中文名字原样保留。");
        Assert.AreEqual("Daily Musings", SmtpSettings.UsableFromName("  Daily Musings  "), "前后空白去掉。");
        Assert.AreEqual("Musing 01", SmtpSettings.UsableFromName("Musing 01"));

        // 真正要挡住的那种值：全是问号、全是标点、或者干脆是空的。
        Assert.AreEqual(SmtpSettings.DefaultFromName, SmtpSettings.UsableFromName("????"));
        Assert.AreEqual(SmtpSettings.DefaultFromName, SmtpSettings.UsableFromName("??? ???"));
        Assert.AreEqual(SmtpSettings.DefaultFromName, SmtpSettings.UsableFromName("..."));
        Assert.AreEqual(SmtpSettings.DefaultFromName, SmtpSettings.UsableFromName("   "));
        Assert.AreEqual(SmtpSettings.DefaultFromName, SmtpSettings.UsableFromName(null));
    }

    /// <summary>
    /// Two saves the form can express but no transport can carry out, both refused here rather than at three in the
    /// morning inside a TLS handshake: both encryption boxes ticked, and a password on a connection with neither box.
    /// Neither refusal may leave half of itself in the settings table.
    /// </summary>
    [TestMethod]
    public async Task Both_encryption_boxes_and_a_password_in_the_clear_are_refused_at_save_time()
    {
        var store = new InMemoryAppSettingStore();
        var update = new UpdateSmtpSettingsUseCase(store, new InMemoryUiSecretStore(), InMemorySecretStore.Empty());

        var conflicting = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            update.ExecuteAsync(
                new SmtpSettingsUpdate(true, "smtp.example.com", 465, "a@example.com", UseSsl: true, UseStartTls: true),
                CancellationToken.None));

        Assert.AreEqual("smtp.security.conflicting", conflicting.Code);
        Assert.AreEqual(0, store.Snapshot().Count, "A refused save writes nothing at all.");

        var inTheClear = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            update.ExecuteAsync(
                new SmtpSettingsUpdate(true, "smtp.example.com", 25, "a@example.com", UseSsl: false, UseStartTls: false, Password: "s3cret"),
                CancellationToken.None));

        Assert.AreEqual("smtp.security.credentials_in_clear", inTheClear.Code);
        Assert.AreEqual(0, store.Snapshot().Count, "Nothing may be stored, and no password may be filed either.");

        // No password and no encryption is the configuration a relay on localhost wants, and it saves.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(true, "127.0.0.1", 1025, "dailymusings@localhost", UseSsl: false, UseStartTls: false),
            CancellationToken.None);

        Assert.AreEqual("false", store.Snapshot()[SmtpSettingKeys.Ssl]);
        Assert.AreEqual("false", store.Snapshot()[SmtpSettingKeys.StartTls]);
    }

    /// <summary>
    /// An instance that already has a password cannot have its encryption switched off — the password would then be
    /// offered on a connection anyone can read. Clearing the password in the same breath is the way out, because
    /// afterwards there is nothing to leak.
    /// </summary>
    [TestMethod]
    public async Task Turning_encryption_off_is_refused_while_a_password_exists_and_allowed_once_it_is_cleared()
    {
        var store = new InMemoryAppSettingStore();
        var secrets = InMemorySecretStore.Empty().With("smtp-password", "already-provisioned");
        var update = new UpdateSmtpSettingsUseCase(store, new InMemoryUiSecretStore(), secrets);

        // A stored "yes, encrypted" pair, as the page would have written it.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, 465, null, UseSsl: true, UseStartTls: false),
            CancellationToken.None);

        var turnedOff = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
            update.ExecuteAsync(
                new SmtpSettingsUpdate(null, null, null, null, UseSsl: false, UseStartTls: false),
                CancellationToken.None));

        Assert.AreEqual("smtp.security.credentials_in_clear", turnedOff.Code);
        Assert.AreEqual("true", store.Snapshot()[SmtpSettingKeys.Ssl], "The refused save left the switch as it was.");

        // Clearing the stored password at the same time makes the unencrypted configuration legal: there is no
        // credential left to expose.
        await update.ExecuteAsync(
            new SmtpSettingsUpdate(null, null, null, null, UseSsl: false, UseStartTls: false, ClearPassword: true),
            CancellationToken.None);

        Assert.AreEqual("false", store.Snapshot()[SmtpSettingKeys.Ssl]);
        Assert.AreEqual("false", store.Snapshot()[SmtpSettingKeys.StartTls]);
    }
}
