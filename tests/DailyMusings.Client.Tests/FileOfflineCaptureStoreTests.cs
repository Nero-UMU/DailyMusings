using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Offline;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The device's own archive (docs/开发指导.md §9.2). What matters here is durability: a capture the user was told is
/// saved must still be there after the process dies, and its audio must never be lost — not while it is waiting to
/// be uploaded, and not after the server has confirmed it either.
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
    public async Task The_archive_lists_newest_first_so_the_calendar_starts_where_the_user_left_off()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);
            await store.UpsertAsync(Capture("late", new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero)), CancellationToken.None);
            await store.UpsertAsync(Capture("early", new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero)), CancellationToken.None);

            var queue = await store.ListAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "late", "early" }, queue.Select(c => c.Id).ToArray());
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
    public async Task Only_a_capture_the_server_has_confirmed_stops_waiting_to_be_uploaded()
    {
        var queued = Capture("c1", DateTimeOffset.UnixEpoch);
        var uploaded = queued with { State = CaptureUploadState.Uploaded };
        var failed = queued with { State = CaptureUploadState.Failed, FailureCode = "client.timeout" };

        Assert.IsTrue(queued.NeedsUpload);
        Assert.IsFalse(uploaded.NeedsUpload, "A confirmed capture is no longer outstanding — but it is still here.");
        Assert.IsTrue(failed.NeedsUpload, "A failure is retryable, so the row is still outstanding.");

        await Task.CompletedTask;
    }

    /// <summary>
    /// The store has to keep reading the records written before the phone kept transcripts in them. Those files have
    /// no <c>transcript</c> or <c>transcriptionAtUtc</c> field at all, and an upgrade that could not read them would
    /// silently lose every thought the user ever recorded.
    /// </summary>
    [TestMethod]
    public async Task A_record_written_before_the_new_fields_existed_is_still_readable()
    {
        var root = NewRoot();

        try
        {
            var store = new FileOfflineCaptureStore(root);

            var legacy = """
                {"id":"legacy","idempotencyKey":"key-legacy","kind":0,"localAudioPath":null,"text":"旧记录。",
                "createdAtUtc":"2026-02-28T10:00:00+00:00","createdOffsetMinutes":480,"durationSeconds":null,
                "state":1,"attemptCount":0,"failureCode":null,"failureSummary":null,"serverInputId":"server-9",
                "uploadedAtUtc":"2026-02-28T11:00:00+00:00","holdsOnlyCopy":false}
                """;

            await File.WriteAllTextAsync(Path.Combine(store.RecordDirectory, "legacy.json"), legacy, Encoding.UTF8);

            var capture = await store.FindAsync("legacy", CancellationToken.None);

            Assert.IsNotNull(capture);
            Assert.AreEqual("旧记录。", capture.Text);
            Assert.IsNull(capture.Transcript, "A record with no transcript reads as null rather than failing.");
            Assert.IsNull(capture.TranscriptionAtUtc);
            Assert.AreEqual(CaptureUploadState.Uploaded, capture.State);
            Assert.IsFalse(capture.NeedsUpload);
            Assert.AreEqual("旧记录。", capture.DisplayText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
