using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// Which layer wins, uniformly (docs/开发指导.md §8.1).
/// <para>
/// The rule is "what the admin page saved, else the deployment configuration, else the built-in default". Two
/// settings used to break it: the notification recipient, where configuration won — so an operator could turn an
/// event off and keep receiving it — and retrieval tuning, which configuration alone owned and which the provider
/// cached in its constructor, so even a compose edit needed a restart. These tests hold the uniform rule in place.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SettingsPrecedenceApiTests
{
    [TestMethod]
    public async Task The_stored_notification_recipient_wins_over_the_deployment_configuration()
    {
        await using var instance = await TestInstance.StartAsync(new Dictionary<string, string?>
        {
            ["Notification:To"] = "from-compose@example.test",
            ["Notification:InstanceUrl"] = "https://from-compose.example.test",
        });

        await instance.SignInAsChangedAdministratorAsync();

        // Deployment configuration is the fallback while the admin page has never been used.
        var configured = await instance.Client.GetFromJsonAsync<NotificationSettingsDto>("/api/notification-settings");

        Assert.IsNotNull(configured);
        Assert.AreEqual("from-compose@example.test", configured.ToAddress);
        Assert.AreEqual("https://from-compose.example.test", configured.InstanceUrl);

        using var saved = await instance.Client.PatchAsJsonAsync(
            "/api/notification-settings",
            new UpdateNotificationSettingsRequest("from-admin-page@example.test", null, true, null, null, null));

        saved.EnsureSuccessStatusCode();

        var afterSave = await instance.Client.GetFromJsonAsync<NotificationSettingsDto>("/api/notification-settings");

        Assert.IsNotNull(afterSave);
        Assert.AreEqual("from-admin-page@example.test", afterSave.ToAddress, "The admin page must win.");
        Assert.AreEqual(
            "https://from-compose.example.test",
            afterSave.InstanceUrl,
            "A field the page left alone keeps the configured value.");
        Assert.IsTrue(afterSave.DraftReady, "A stored switch overrides the configuration's default.");
    }

    [TestMethod]
    public async Task Retrieval_settings_fall_back_to_configuration_and_then_yield_to_the_admin_page()
    {
        await using var instance = await TestInstance.StartAsync(new Dictionary<string, string?>
        {
            ["Retrieval:MaxMaterials"] = "42",
        });

        await instance.SignInAsChangedAdministratorAsync();

        var fromConfiguration = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>("/api/system/instance-settings");

        Assert.IsNotNull(fromConfiguration);
        Assert.AreEqual(42, fromConfiguration.RetrievalMaxMaterials, "Deployment configuration is the fallback.");
        Assert.AreEqual(
            500,
            fromConfiguration.RetrievalCandidateScanLimit,
            "A value neither layer sets comes from the built-in default.");

        using var saved = await instance.Client.PatchAsJsonAsync(
            "/api/system/instance-settings",
            new UpdateInstanceSettingsRequest(
                SchedulerIntervalSeconds: null,
                SchedulerBackfillWindowDays: null,
                SchedulerMaxGenerationsPerTick: null,
                BackupEnabled: null,
                BackupLocalTime: null,
                AudioCleanupLocalTime: null,
                BackupKeepCount: null,
                RetrievalMaxMaterials: 7,
                RetrievalCandidateScanLimit: null,
                RetrievalMinimumRelevance: null,
                RetrievalMinimumLexicalScore: null));

        saved.EnsureSuccessStatusCode();

        var afterSave = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>("/api/system/instance-settings");

        Assert.IsNotNull(afterSave);
        Assert.AreEqual(7, afterSave.RetrievalMaxMaterials, "The admin page wins over the deployment configuration.");
    }
}
