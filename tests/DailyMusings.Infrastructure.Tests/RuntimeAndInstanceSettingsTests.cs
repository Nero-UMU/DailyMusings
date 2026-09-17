using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// §8.1's "everything configurable from the admin page": the operational settings that used to live only in the
/// deployment configuration, and the two things that cannot — the listening port, which has to be known before the
/// web host exists, and the per-target WordPress site, which is a credential reference.
/// <para>
/// The behaviours asserted here were each observed against a running instance before being written down, including
/// the failure paths: a corrupt override file, an unbindable port, and a cleared override falling back to the
/// deployment configuration.
/// </para>
/// </summary>
[TestClass]
public class RuntimeAndInstanceSettingsTests
{
    private const string IgnoreVariable = RuntimeOverridesFile.IgnoreVariableName;

    /// <summary>
    /// These are the numbers the guide documents, so a fresh instance behaves as the manual describes. They were
    /// read back from <c>GET /api/system/instance-settings</c> on an unconfigured instance.
    /// </summary>
    [TestMethod]
    public void An_unconfigured_instance_uses_the_documented_defaults()
    {
        var defaults = InstanceSettings.Default;

        Assert.AreEqual(30, defaults.SchedulerIntervalSeconds, "The scheduler ticks every 30s by default.");
        Assert.AreEqual(60, defaults.SchedulerBackfillWindowDays);
        Assert.AreEqual(3, defaults.SchedulerMaxGenerationsPerTick);
        Assert.IsTrue(defaults.BackupEnabled, "The nightly backup is on unless an operator turns it off.");
        Assert.AreEqual(new TimeOnly(3, 30), defaults.BackupLocalTime);
        Assert.AreEqual(new TimeOnly(4, 0), defaults.AudioCleanupLocalTime);
        Assert.AreEqual(7, defaults.BackupKeepCount, "§15.2 keeps the newest seven archives.");
        Assert.AreEqual(10, defaults.RetrievalMaxMaterials);
        Assert.AreEqual(500, defaults.RetrievalCandidateScanLimit);
        Assert.AreEqual(0.05, defaults.RetrievalMinimumRelevance);
        Assert.AreEqual(0.08, defaults.RetrievalMinimumLexicalScore);

        // An empty settings table has to mean exactly this.
        Assert.AreEqual(defaults, InstanceSettings.FromValues(new Dictionary<string, string>()));
    }

    /// <summary>
    /// The record is persisted as key/value pairs, so its two directions have to agree — a value that survives
    /// being written and read back differently would silently change an operator's setting on the next restart.
    /// </summary>
    [TestMethod]
    public void Stored_values_round_trip_and_win_over_the_defaults()
    {
        var settings = new InstanceSettings(
            schedulerIntervalSeconds: 45,
            schedulerBackfillWindowDays: 14,
            schedulerMaxGenerationsPerTick: 1,
            backupEnabled: false,
            backupLocalTime: new TimeOnly(2, 15),
            audioCleanupLocalTime: new TimeOnly(5, 45),
            backupKeepCount: 9,
            retrievalMaxMaterials: 8,
            retrievalCandidateScanLimit: 2_000,
            retrievalMinimumRelevance: 0.31,
            retrievalMinimumLexicalScore: 0.12);

        var readBack = InstanceSettings.FromValues(settings.ToValues());

        Assert.AreEqual(settings, readBack);

        // The two times travel as HH:mm, which is what the admin page binds to.
        Assert.AreEqual("02:15", settings.ToValues()[InstanceSettings.BackupLocalTimeKey]);
        Assert.AreEqual("05:45", settings.ToValues()[InstanceSettings.AudioCleanupLocalTimeKey]);

        // A single stored key must not disturb the others.
        var partial = InstanceSettings.FromValues(new Dictionary<string, string>
        {
            [InstanceSettings.RetrievalMaxMaterialsKey] = "25",
        });

        Assert.AreEqual(25, partial.RetrievalMaxMaterials);
        Assert.AreEqual(InstanceSettings.Default.SchedulerIntervalSeconds, partial.SchedulerIntervalSeconds);
    }

    /// <summary>
    /// The escape hatches, and the reason they exist: a bad port must never leave an instance unreachable. Anything
    /// unreadable, malformed or unbindable is treated as "no override" rather than as a reason to fail.
    /// </summary>
    [TestMethod]
    public void An_unusable_override_file_means_no_override_rather_than_a_dead_instance()
    {
        var root = NewTempDirectory();

        try
        {
            var path = Path.Combine(root, "runtime.json");

            // Missing.
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            // Corrupt: not JSON at all.
            File.WriteAllText(path, "{ this is not valid json");
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            // Valid JSON, unbindable port: the container runs unprivileged, so below 1024 would fail to bind.
            File.WriteAllText(path, "{ \"port\": 80 }");
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            // Out of range the other way, and not even a number.
            File.WriteAllText(path, "{ \"port\": 70000 }");
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            File.WriteAllText(path, "{ \"port\": \"18321\" }");
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            // The override the operator cleared is written as null and reads back as "not set".
            File.WriteAllText(path, "{ \"port\": null }");
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);

            // And none of that may throw.
            Assert.AreEqual(RuntimeOverrides.None.ListeningPort, RuntimeOverridesFile.Read(path).ListeningPort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void A_valid_override_is_read_back_after_being_written()
    {
        var root = NewTempDirectory();
        var previous = Environment.GetEnvironmentVariable(IgnoreVariable);

        try
        {
            // The ignore switch is a real escape hatch, so a machine that has it set must not change these results.
            Environment.SetEnvironmentVariable(IgnoreVariable, null);

            var path = Path.Combine(root, "runtime.json");
            var at = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

            RuntimeOverridesFile.Write(path, new RuntimeOverrides(19464, at, "owner"));

            var read = RuntimeOverridesFile.Read(path);

            Assert.AreEqual(19464, read.ListeningPort);
            Assert.AreEqual("owner", read.UpdatedBy);
            Assert.AreEqual(at, read.UpdatedAtUtc);

            // Written through a temporary file and renamed, so nothing half-written is ever left behind.
            Assert.IsFalse(File.Exists(path + ".partial"), "The staging file must not survive a completed write.");

            // Rewriting replaces the value rather than appending to it, and clearing removes the override.
            RuntimeOverridesFile.Write(path, new RuntimeOverrides(null, at, "owner"));
            Assert.IsNull(RuntimeOverridesFile.Read(path).ListeningPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IgnoreVariable, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void An_unbindable_port_is_refused_before_it_reaches_the_file()
    {
        var root = NewTempDirectory();

        try
        {
            var path = Path.Combine(root, "runtime.json");
            var at = DateTimeOffset.UnixEpoch;

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => RuntimeOverridesFile.Write(path, new RuntimeOverrides(RuntimeOverridesFile.MinimumPort - 1, at, "owner")));

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => RuntimeOverridesFile.Write(path, new RuntimeOverrides(RuntimeOverridesFile.MaximumPort + 1, at, "owner")));

            // The refused writes left nothing behind, so the instance still has its previous answer.
            Assert.IsFalse(File.Exists(path));

            // The bounds themselves are legal.
            RuntimeOverridesFile.Write(path, new RuntimeOverrides(RuntimeOverridesFile.MinimumPort, at, "owner"));
            Assert.AreEqual(RuntimeOverridesFile.MinimumPort, RuntimeOverridesFile.Read(path).ListeningPort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Keyed by the target's id, not its name: renaming a target must not move one site's credentials onto another
    /// site, and the keys have to stay safe to persist and to select in the settings table.
    /// </summary>
    [TestMethod]
    public void WordPress_site_keys_are_per_target_and_free_of_path_characters()
    {
        var first = new PublishTargetId(Guid.CreateVersion7());
        var second = new PublishTargetId(Guid.CreateVersion7());

        var keys = new[]
        {
            WordPressSiteSettingKeys.BaseUrl(first),
            WordPressSiteSettingKeys.Username(first),
            WordPressSiteSettingKeys.SecretName(first),
            WordPressSiteSettingKeys.TimeoutSeconds(first),
        };

        Assert.AreEqual(4, keys.Distinct(StringComparer.Ordinal).Count(), "Each field needs its own key.");

        foreach (var key in keys)
        {
            StringAssert.Contains(key, first.Value.ToString("N"), "The key must carry the target's id.");
            Assert.IsFalse(key.Contains('/', StringComparison.Ordinal), key);
            Assert.IsFalse(key.Contains('\\', StringComparison.Ordinal), key);
        }

        Assert.AreNotEqual(
            WordPressSiteSettingKeys.BaseUrl(first),
            WordPressSiteSettingKeys.BaseUrl(second),
            "Two targets must not share a site address.");
    }

    /// <summary>
    /// A blank field means "stop overriding this one", so blankness has to be recognised as such — otherwise the
    /// deployment configuration could never take over again.
    /// </summary>
    [TestMethod]
    public void A_blank_wordpress_override_is_recognised_as_empty()
    {
        Assert.IsTrue(WordPressSiteOverride.None.IsEmpty);
        Assert.IsTrue(new WordPressSiteOverride(null, null, null, null).IsEmpty);
        Assert.IsTrue(new WordPressSiteOverride(string.Empty, "   ", null, null).IsEmpty);

        Assert.IsFalse(new WordPressSiteOverride("https://blog.example.com", null, null, null).IsEmpty);
        Assert.IsFalse(new WordPressSiteOverride(null, "owner", null, null).IsEmpty);
        Assert.IsFalse(new WordPressSiteOverride(null, null, "wordpress-application-password", null).IsEmpty);
        Assert.IsFalse(new WordPressSiteOverride(null, null, null, 45).IsEmpty);
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "dailymusings-runtime",
            Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }
}
