using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// §8.1's operational settings and the one setting that cannot live in the settings table, over the real API.
/// <para>
/// The listening port is the interesting half: a container's published mapping is fixed when the container starts,
/// so the honest answer to "save this port" is "saved, and here is what it takes to apply it" — never "done". These
/// tests pin that answer down, along with the refusal to store a port the instance could not bind.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class InstanceSettingsApiTests
{
    private const string SettingsPath = "/api/system/instance-settings";
    private const string PortPath = "/api/system/listening-port";

    [TestMethod]
    public async Task An_administrator_can_read_and_change_the_operational_settings()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var initial = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>(SettingsPath);

        Assert.IsNotNull(initial);
        Assert.AreEqual(30, initial.SchedulerIntervalSeconds, "The documented default, on an unconfigured instance.");
        Assert.AreEqual(7, initial.BackupKeepCount, "§15.2 keeps the newest seven archives.");
        Assert.AreEqual("03:30", initial.BackupLocalTime);
        Assert.IsTrue(initial.BackupEnabled);

        using var updated = await instance.Client.PatchAsJsonAsync(
            SettingsPath,
            new UpdateInstanceSettingsRequest(
                SchedulerIntervalSeconds: 45,
                SchedulerBackfillWindowDays: 14,
                SchedulerMaxGenerationsPerTick: 2,
                BackupEnabled: false,
                BackupLocalTime: "02:15",
                AudioCleanupLocalTime: "05:45",
                BackupKeepCount: 9,
                RetrievalMaxMaterials: 8,
                RetrievalCandidateScanLimit: 250,
                RetrievalMinimumRelevance: 0.31,
                RetrievalMinimumLexicalScore: 0.12));

        updated.EnsureSuccessStatusCode();
        var saved = await updated.Content.ReadFromJsonAsync<InstanceSettingsDto>();

        Assert.IsNotNull(saved);
        Assert.AreEqual(45, saved.SchedulerIntervalSeconds);
        Assert.AreEqual(9, saved.BackupKeepCount);
        Assert.AreEqual("02:15", saved.BackupLocalTime);
        Assert.AreEqual("05:45", saved.AudioCleanupLocalTime);
        Assert.IsFalse(saved.BackupEnabled);

        // A fresh read has to agree: the change is stored, not merely echoed back.
        var reread = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>(SettingsPath);

        Assert.IsNotNull(reread);
        Assert.AreEqual(45, reread.SchedulerIntervalSeconds);
        Assert.AreEqual(14, reread.SchedulerBackfillWindowDays);
        Assert.AreEqual(0.31, reread.RetrievalMinimumRelevance);
        Assert.AreEqual(250, reread.RetrievalCandidateScanLimit);
    }

    /// <summary>
    /// The whole set is validated before any of it is written. The model and SMTP editors store field by field, so a
    /// rejected field there leaves the earlier ones applied — an operator sees an error and a half-changed instance.
    /// These settings deliberately do not behave that way, and this is the test that holds that line.
    /// </summary>
    [TestMethod]
    public async Task An_out_of_range_value_is_refused_and_no_other_field_is_written()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.PatchAsJsonAsync(
            SettingsPath,
            new UpdateInstanceSettingsRequest(
                SchedulerIntervalSeconds: 0,
                SchedulerBackfillWindowDays: null,
                SchedulerMaxGenerationsPerTick: null,
                BackupEnabled: null,
                BackupLocalTime: null,
                AudioCleanupLocalTime: null,
                BackupKeepCount: 3,
                RetrievalMaxMaterials: null,
                RetrievalCandidateScanLimit: null,
                RetrievalMinimumRelevance: null,
                RetrievalMinimumLexicalScore: null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("instance.scheduler_interval.invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var after = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>(SettingsPath);

        Assert.IsNotNull(after);
        Assert.AreEqual(30, after.SchedulerIntervalSeconds, "The refused value must not have been stored.");
        Assert.AreEqual(7, after.BackupKeepCount, "And neither must the field that travelled with it.");
    }

    [TestMethod]
    public async Task An_ill_formed_time_is_refused_before_it_reaches_the_use_case()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.PatchAsJsonAsync(
            SettingsPath,
            new UpdateInstanceSettingsRequest(null, null, null, null, "25:99", null, null, null, null, null, null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(ApiErrorCodes.ValidationFailed, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // The other field being absent means "leave it", which is why a single bad field cannot clear anything.
        var after = await instance.Client.GetFromJsonAsync<InstanceSettingsDto>(SettingsPath);
        Assert.AreEqual("03:30", after!.BackupLocalTime);
    }

    [TestMethod]
    public async Task A_paired_device_cannot_read_or_change_the_instance_settings()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var (_, device) = await instance.PairDeviceAsync();

        using var read = await device.GetAsync(SettingsPath);
        Assert.IsTrue(
            read.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A capture credential must not read the instance's tuning (got {read.StatusCode}).");

        using var portRead = await device.GetAsync(PortPath);
        Assert.IsTrue(
            portRead.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"…nor which port it listens on (got {portRead.StatusCode}).");

        using var write = await device.PatchAsJsonAsync(
            SettingsPath,
            new UpdateInstanceSettingsRequest(3_600, null, null, null, null, null, null, null, null, null, null));

        Assert.IsTrue(
            write.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device must not change how the instance is maintained (got {write.StatusCode}).");
    }

    /// <summary>
    /// The port override, end to end: it is written where the next start will read it, it is reported as pending
    /// rather than applied, and a port the instance could not bind is refused instead of stored.
    /// </summary>
    [TestMethod]
    public async Task The_listening_port_override_is_stored_and_reported_as_pending_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-api", Guid.CreateVersion7().ToString("N"));
        var configPath = Path.Combine(root, "runtime.json");

        // Pointed at the throwaway instance directory, so the file this test writes is cleaned up with the instance
        // rather than landing in whatever directory the test host happens to run from.
        await using var instance = await TestInstance.StartAtAsync(
            root,
            new Dictionary<string, string?> { ["Storage:RuntimeConfigPath"] = configPath });

        await instance.SignInAsChangedAdministratorAsync();

        var before = await instance.Client.GetFromJsonAsync<ListeningPortDto>(PortPath);

        Assert.IsNotNull(before);
        Assert.IsNull(before.OverridePort, "Nothing has been overridden on a fresh instance.");
        Assert.IsFalse(before.RestartRequired);
        Assert.IsTrue(before.EffectivePort > 0, "The instance is listening on a real socket.");
        Assert.AreEqual(configPath, before.RuntimeConfigPath, "The path a locked-out operator would have to edit.");

        var requested = before.EffectivePort == 19464 ? 19465 : 19464;

        using var saved = await instance.Client.PatchAsJsonAsync(PortPath, new UpdateListeningPortRequest(requested));
        saved.EnsureSuccessStatusCode();

        var pending = await saved.Content.ReadFromJsonAsync<ListeningPortDto>();

        Assert.IsNotNull(pending);
        Assert.AreEqual(requested, pending.OverridePort);
        Assert.AreEqual(before.EffectivePort, pending.EffectivePort, "This instance is still on the port it bound.");
        Assert.IsTrue(pending.RestartRequired, "A container's published mapping is fixed at start, so this is pending.");

        Assert.IsTrue(File.Exists(configPath), "The next start can only learn this from the file.");
        StringAssert.Contains(File.ReadAllText(configPath), requested.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // An unbindable port is refused before anything is written: the container runs unprivileged.
        using var refused = await instance.Client.PatchAsJsonAsync(PortPath, new UpdateListeningPortRequest(80));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual(ApiErrorCodes.ValidationFailed, (await refused.Content.ReadFromJsonAsync<ApiError>())!.Code);
        StringAssert.Contains(
            File.ReadAllText(configPath),
            requested.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "The refused write must not have replaced what was already saved.");

        // Clearing the override puts the instance back on the deployment's own port.
        using var cleared = await instance.Client.PatchAsJsonAsync(PortPath, new UpdateListeningPortRequest(null));
        cleared.EnsureSuccessStatusCode();
        Assert.IsNull((await cleared.Content.ReadFromJsonAsync<ListeningPortDto>())!.OverridePort);
        Assert.IsFalse((await instance.Client.GetFromJsonAsync<ListeningPortDto>(PortPath))!.RestartRequired);
    }

    /// <summary>
    /// A container deployment owns the port: the published mapping is fixed when the container starts, so the only
    /// honest answer to "save this port" is a refusal. It also ignores an override an earlier start left behind —
    /// the shipped Compose file sets this lock, and an instance that honoured a stale file would come back up on a
    /// port nothing is mapped to.
    /// </summary>
    [TestMethod]
    public async Task A_deployment_that_owns_the_port_reports_it_and_refuses_an_override()
    {
        var variable = RuntimeOverridesFile.ListeningPortLockVariableName;
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-api", Guid.CreateVersion7().ToString("N"));
        var configPath = Path.Combine(root, "runtime.json");

        try
        {
            Environment.SetEnvironmentVariable(variable, "1");

            Directory.CreateDirectory(root);
            File.WriteAllText(configPath, "{ \"port\": 19464, \"updatedBy\": \"owner\" }");

            await using var instance = await TestInstance.StartAtAsync(
                root,
                new Dictionary<string, string?> { ["Storage:RuntimeConfigPath"] = configPath });

            await instance.SignInAsChangedAdministratorAsync();

            var current = await instance.Client.GetFromJsonAsync<ListeningPortDto>(PortPath);

            Assert.IsNotNull(current);
            Assert.IsTrue(current.Locked, "The page needs this to stop offering an edit it cannot apply.");
            Assert.IsNull(current.OverridePort, "A stale override must not survive the lock.");
            Assert.IsFalse(current.RestartRequired, "Nothing is pending when nothing can be saved.");

            using var refused = await instance.Client.PatchAsJsonAsync(
                PortPath,
                new UpdateListeningPortRequest(19465));

            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.AreEqual(
                ApiErrorCodes.InstancePortLocked,
                (await refused.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
