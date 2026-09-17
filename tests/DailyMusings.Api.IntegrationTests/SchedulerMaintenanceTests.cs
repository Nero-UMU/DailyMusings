using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The scheduler's two halves are independent (docs/开发指导.md §7, §15.1, §15.2).
/// <para>
/// Generating a draft needs a model endpoint; taking the nightly backup does not. A fresh instance has no model
/// endpoint configured, and the scheduler used to return early in exactly that state — so on a default deployment
/// the nightly backup, the audio sweep and the publication window never ran at all. The settings page offered a
/// backup slot that nothing would ever honour, which is the kind of defect that only shows up as "I thought it was
/// backing up".
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SchedulerMaintenanceTests
{
    [TestMethod]
    public async Task The_nightly_backup_is_scheduled_even_when_no_model_endpoint_is_configured()
    {
        // A one-second tick and a slot that has always arrived: deterministic, rather than dependent on the wall
        // clock the suite happens to run at. Note what is *not* configured — there is no generation endpoint, which
        // is the state the test is about.
        await using var instance = await TestInstance.StartAsync(new Dictionary<string, string?>
        {
            ["Scheduler:IntervalSeconds"] = "1",
            ["Maintenance:BackupLocalTime"] = "00:00",
            ["Maintenance:AudioCleanupLocalTime"] = "00:00",
        });

        await instance.SignInAsChangedAdministratorAsync();

        var backup = await WaitForJobAsync(instance, "backup");

        Assert.IsNotNull(
            backup,
            "The scheduler must queue today's backup without a generation endpoint; returning early skipped it.");

        StringAssert.Contains(backup.TargetId, "-", "The job is keyed on the content day (YYYY-MM-DD).");

        // And the sweep, which sits in the same maintenance half and was skipped for the same reason.
        var cleanup = await WaitForJobAsync(instance, "audiocleanup");

        Assert.IsNotNull(cleanup, "The audio sweep must be scheduled too.");
    }

    /// <summary>
    /// The scheduler runs on its own timer, so polling is the honest way to observe a background tick rather than
    /// reaching into the service.
    /// </summary>
    private static async Task<JobDto?> WaitForJobAsync(TestInstance instance, string jobType)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var jobs = await instance.Client.GetFromJsonAsync<JobListResponse>("/api/jobs");

            var match = jobs?.Items.FirstOrDefault(
                job => string.Equals(job.JobType, jobType, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(500);
        }

        return null;
    }
}
