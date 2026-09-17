using System.Net;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The two things an operator could previously only do from a shell on the host: look at what the instance holds,
/// and take a copy of it away (docs/开发指导.md §8.2, §15.1, §15.2).
/// <para>
/// Both are administrator-only, and both are asserted against the real pipeline rather than a handler in isolation —
/// the permission split is the interesting half, because a capture credential must not be able to read every
/// recorded thought or walk off with the database.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class AdminContentAndDownloadsApiTests
{
    [TestMethod]
    public async Task The_capture_page_renders_for_an_administrator_and_not_for_a_stranger()
    {
        await using var instance = await TestInstance.StartAsync();

        using var anonymous = await instance.Client.GetAsync("/inputs");
        Assert.IsTrue(
            anonymous.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"An anonymous visitor must not read captured content (got {anonymous.StatusCode}).");

        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/inputs");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(html, "采集");
        StringAssert.Contains(html, "当天的草稿", "The page has to show the draft as well as the captures.");
    }

    [TestMethod]
    public async Task A_backup_can_be_downloaded_by_an_administrator_and_by_nobody_else()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var created = await instance.PostForJsonAsync<BackupDto>("/api/backups");

        Assert.IsNotNull(created);
        Assert.IsFalse(string.IsNullOrWhiteSpace(created.FileName));

        var path = $"/api/backups/{created.FileName}/download";

        using var response = await instance.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.IsTrue(bytes.Length > 4, "A backup is not empty.");
        Assert.AreEqual((byte)'P', bytes[0], "The download has to be the archive itself, not an error page.");
        Assert.AreEqual((byte)'K', bytes[1]);

        // A name that is not a package on disk is a 404, not an exception.
        using var missing = await instance.Client.GetAsync("/api/backups/no-such-backup.zip/download");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        // A device token is a credential for capturing, not for walking off with the instance.
        var (_, device) = await instance.PairDeviceAsync();

        using var deviceRead = await device.GetAsync(path);
        Assert.IsTrue(
            deviceRead.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A capture credential must not download the whole instance (got {deviceRead.StatusCode}).");
    }

    [TestMethod]
    public async Task An_export_can_be_downloaded_as_a_zip()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var created = await instance.PostForJsonAsync<InstanceExportDto>("/api/exports");

        Assert.IsNotNull(created);
        Assert.IsFalse(string.IsNullOrWhiteSpace(created.RelativeRoot));

        using var response = await instance.Client.GetAsync($"/api/exports/{created.RelativeRoot}/download");
        response.EnsureSuccessStatusCode();

        Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();

        // An export is a directory on disk, so the endpoint archives it on demand.
        Assert.IsTrue(bytes.Length > 4);
        Assert.AreEqual((byte)'P', bytes[0]);
        Assert.AreEqual((byte)'K', bytes[1]);

        using var missing = await instance.Client.GetAsync("/api/exports/no-such-export/download");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// The name arrives from a URL, so it is not trusted: anything with a path separator, a parent reference or an
    /// invalid character is refused rather than resolved.
    /// </summary>
    [TestMethod]
    public async Task A_download_name_that_tries_to_walk_the_filesystem_is_refused()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        foreach (var attempt in new[]
                 {
                     "/api/backups/..%2Fdailymusings.db/download",
                     "/api/backups/%2e%2e%2fdailymusings.db/download",
                     "/api/exports/..%2F..%2Fdata/download",
                 })
        {
            using var response = await instance.Client.GetAsync(attempt);

            Assert.IsTrue(
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
                $"{attempt} must be refused, not resolved (got {response.StatusCode}).");
        }
    }
}
