using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Offline;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The device-side queue (docs/开发指导.md §9.2). What matters here is durability: a capture the user was told is
/// saved must still be there after the process dies, and its audio must never be lost while it is still the only
/// copy.
/// </summary>
[TestClass]
public class FileOfflineCaptureStoreTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dailymusings-client", Guid.CreateVersion7().ToString("N"));

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static PendingCapture Capture(
        string id,
        DateTimeOffset createdAtUtc,
        CaptureKind kind = CaptureKind.Text) => new()
        {
            Id = id,
            IdempotencyKey = $"key-{id}",
            Kind = kind,
            Text = kind == CaptureKind.Text ? "一句话。" : null,
            CreatedAtUtc = createdAtUtc,
            CreatedOffsetMinutes = 480,
            State = CaptureUploadState.Queued,
        };

    [TestMethod]
    public async Task Audio_is_on_disk_by_the_time_saving_returns()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);

            var path = await store.SaveAudioAsync("capture-1", Bytes("audio"), ".m4a", CancellationToken.None);

            // The point of §9.2: this must be true the instant the call returns, with no further waiting.
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual("audio", await File.ReadAllTextAsync(path));
            StringAssert.EndsWith(path, "capture-1.m4a");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_queued_capture_survives_a_restart_of_the_store()
    {
        var root = NewRoot();

        try
        {
            var first = new FileOfflineCaptureStore(root);
            await first.UpsertAsync(Capture("c1", new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero)), CancellationToken.None);

            // A new instance stands in for the app being killed and relaunched.
            var reopened = new FileOfflineCaptureStore(root);
            var queue = await reopened.ListAsync(CancellationToken.None);

            Assert.AreEqual(1, queue.Count);
            Assert.AreEqual("c1", queue[0].Id);
            Assert.AreEqual("key-c1", queue[0].IdempotencyKey);
            Assert.AreEqual(CaptureUploadState.Queued, queue[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task The_queue_reads_oldest_first_so_thoughts_arrive_in_order()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);
            await store.UpsertAsync(Capture("late", new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero)), CancellationToken.None);
            await store.UpsertAsync(Capture("early", new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero)), CancellationToken.None);

            var queue = await store.ListAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "early", "late" }, queue.Select(c => c.Id).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Upsert_replaces_the_record_rather_than_duplicating_it()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);
            var capture = Capture("c1", new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
            await store.UpsertAsync(capture, CancellationToken.None);

            await store.UpsertAsync(
                capture with { AttemptCount = 3, FailureCode = "client.timeout", State = CaptureUploadState.Failed },
                CancellationToken.None);

            var queue = await store.ListAsync(CancellationToken.None);

            Assert.AreEqual(1, queue.Count);
            Assert.AreEqual(3, queue[0].AttemptCount);
            Assert.AreEqual("client.timeout", queue[0].FailureCode);
            Assert.AreEqual(CaptureUploadState.Failed, queue[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Deleting_a_capture_removes_both_the_record_and_its_audio()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);
            var path = await store.SaveAudioAsync("c1", Bytes("audio"), ".m4a", CancellationToken.None);
            await store.UpsertAsync(
                Capture("c1", new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero)) with { LocalAudioPath = path },
                CancellationToken.None);

            await store.DeleteAsync("c1", CancellationToken.None);

            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(0, (await store.ListAsync(CancellationToken.None)).Count);
            Assert.IsNull(await store.FindAsync("c1", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_hostile_file_extension_cannot_steer_where_the_blob_lands()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);

            foreach (var extension in new[] { "../../escape", "audio/mp4", string.Empty, ".m4a" })
            {
                var path = await store.SaveAudioAsync($"c-{Guid.CreateVersion7():N}", Bytes("x"), extension, CancellationToken.None);

                Assert.AreEqual(store.AudioDirectory, Path.GetDirectoryName(path), $"extension '{extension}'");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_capture_that_has_not_been_confirmed_reports_that_it_holds_the_only_copy()
    {
        var queued = Capture("c1", DateTimeOffset.UnixEpoch);
        var uploaded = queued with { State = CaptureUploadState.Uploaded };

        Assert.IsTrue(queued.HoldsOnlyCopy);
        Assert.IsFalse(uploaded.HoldsOnlyCopy);

        await Task.CompletedTask;
    }
}
