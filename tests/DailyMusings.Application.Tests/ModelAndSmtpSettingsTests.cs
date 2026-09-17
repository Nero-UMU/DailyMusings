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
        (new UpdateModelEndpointUseCase(new InMemoryAppSettingStore()), new InMemoryAppSettingStore());

    [TestMethod]
    public async Task Saving_an_endpoint_writes_every_field_the_guide_lists()
    {
        var store = new InMemoryAppSettingStore();

        await new UpdateModelEndpointUseCase(store).ExecuteAsync(
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
        var update = new UpdateModelEndpointUseCase(store);

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
        var update = new UpdateModelEndpointUseCase(new InMemoryAppSettingStore());

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

        await new UpdateModelEndpointUseCase(store).ExecuteAsync(
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
    public async Task Smtp_settings_are_written_and_validated_the_same_way()
    {
        var store = new InMemoryAppSettingStore();
        var update = new UpdateSmtpSettingsUseCase(store);

        await update.ExecuteAsync(
            new SmtpSettingsUpdate(
                Enabled: true,
                Host: "smtp.example.com",
                Port: 587,
                UseStartTls: true,
                Username: "owner@example.com",
                SecretName: "smtp-password",
                FromAddress: "dailymusings@example.com",
                FromName: "每日随想",
                TimeoutSeconds: 30),
            CancellationToken.None);

        var written = store.Snapshot();
        Assert.AreEqual("smtp.example.com", written[SmtpSettingKeys.Host]);
        Assert.AreEqual("587", written[SmtpSettingKeys.Port]);
        Assert.AreEqual("true", written[SmtpSettingKeys.UseStartTls]);
        Assert.AreEqual("30", written[SmtpSettingKeys.TimeoutSeconds]);

        // Clearing the username has to be possible: a relay that needs none must be able to lose the one it had.
        await update.ExecuteAsync(new SmtpSettingsUpdate(null, null, null, null, string.Empty, null, null, null, null), CancellationToken.None);
        Assert.AreEqual(string.Empty, store.Snapshot()[SmtpSettingKeys.Username]);

        foreach (var (change, code) in new (SmtpSettingsUpdate, string)[]
                 {
                     (new SmtpSettingsUpdate(null, "bad host", null, null, null, null, null, null, null), "smtp.host.invalid"),
                     (new SmtpSettingsUpdate(null, null, 0, null, null, null, null, null, null), "smtp.port.out_of_range"),
                     (new SmtpSettingsUpdate(null, null, null, null, null, "../secret", null, null, null), "smtp.secret_name.invalid"),
                     (new SmtpSettingsUpdate(null, null, null, null, null, null, "not-an-address", null, null), "smtp.from_address.invalid"),
                     (new SmtpSettingsUpdate(null, null, null, null, null, null, null, null, 1), "smtp.timeout.out_of_range"),
                 })
        {
            var failure = await Assert.ThrowsExceptionAsync<UseCaseException>(() =>
                update.ExecuteAsync(change, CancellationToken.None));

            Assert.AreEqual(code, failure.Code);
        }
    }
}
