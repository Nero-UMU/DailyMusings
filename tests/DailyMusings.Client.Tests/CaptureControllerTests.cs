using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Core.Offline;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The capture façade (docs/开发指导.md §9.2). Two orderings carry the product's promises, and both are asserted
/// rather than assumed: the recording is durable before the UI may say "recorded", and the local copy is deleted
/// only after the server has confirmed it holds the capture.
/// </summary>
[TestClass]
public class CaptureControllerTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dailymusings-capture", Guid.CreateVersion7().ToString("N"));

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static (CaptureController Controller, FileOfflineCaptureStore Store, FakeCaptureApiClient Api) Arrange(
        string root,
        FakeClock? clock = null)
    {
        var store = new FileOfflineCaptureStore(root);
        var api = new FakeCaptureApiClient();

        return (new CaptureController(store, api, clock ?? new FakeClock()), store, api);
    }

    [TestMethod]
    public async Task Saving_a_recording_returns_only_once_the_audio_is_on_disk()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);

            var capture = await controller.SaveVoiceCaptureAsync(
                Bytes("recorded-audio"),
                ".m4a",
                "audio/mp4",
                12.5,
                CancellationToken.None);

            // The claim the UI relies on: after this call, the thought is safe on the device.
            Assert.IsNotNull(capture.LocalAudioPath);
            Assert.IsTrue(File.Exists(capture.LocalAudioPath));
            Assert.AreEqual("recorded-audio", await File.ReadAllTextAsync(capture.LocalAudioPath));
            Assert.AreEqual(CaptureKind.Voice, capture.Kind);
            Assert.AreEqual(CaptureUploadState.Queued, capture.State);
            Assert.AreEqual(12.5, capture.DurationSeconds);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capture.IdempotencyKey));
            Assert.IsTrue(capture.HoldsOnlyCopy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Saving_text_queues_it_without_writing_any_audio()
    {
        var root = NewRoot();

        try
        {
            var (controller, store, _) = Arrange(root);

            var capture = await controller.SaveTextCaptureAsync("  随手记一句。  ", CancellationToken.None);

            Assert.AreEqual(CaptureKind.Text, capture.Kind);
            Assert.AreEqual("随手记一句。", capture.Text);
            Assert.IsNull(capture.LocalAudioPath);
            Assert.AreEqual(0, Directory.EnumerateFiles(store.AudioDirectory).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Syncing_uploads_each_capture_once_then_removes_the_local_copy()
    {
        var root = NewRoot();

        try
        {
            var (controller, store, api) = Arrange(root);
            var voice = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            await controller.SaveTextCaptureAsync("一句话。", CancellationToken.None);

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(2, outcome.Uploaded);
            Assert.AreEqual(0, outcome.Failed);
            Assert.AreEqual(1, api.VoiceUploads.Count);
            Assert.AreEqual(1, api.TextUploads.Count);

            // Both the record and the audio are gone, because the server now holds them.
            Assert.AreEqual(0, (await controller.GetQueueAsync(CancellationToken.None)).Count);
            Assert.IsFalse(File.Exists(voice.LocalAudioPath!));
            Assert.AreEqual(0, Directory.EnumerateFiles(store.AudioDirectory).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The ordering test: a local file deleted before the upload would be a lost thought.</summary>
    [TestMethod]
    public async Task The_local_recording_still_exists_when_the_server_receives_it()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);

            await controller.SyncAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { true },
                api.LocalAudioPresentAtUpload.ToArray(),
                "The device must still hold the audio while the server is receiving it.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_transient_failure_keeps_the_audio_and_records_a_visible_reason()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            api.Failures.Enqueue(new CaptureUploadException("client.network_unreachable", "No network.", transient: true));

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(0, outcome.Uploaded);
            Assert.AreEqual(1, outcome.Failed);

            var queued = await controller.FindAsync(capture.Id, CancellationToken.None);

            Assert.IsNotNull(queued);
            Assert.AreEqual(CaptureUploadState.Failed, queued.State);
            Assert.AreEqual("client.network_unreachable", queued.FailureCode);
            Assert.AreEqual(1, queued.AttemptCount);
            Assert.IsTrue(File.Exists(queued.LocalAudioPath!), "§20: a failing model endpoint must not lose the input.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The dedupe guarantee: a retry must reuse the key, or the server would create a second entry.</summary>
    [TestMethod]
    public async Task A_manual_retry_reuses_the_same_idempotency_key()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            api.Failures.Enqueue(new CaptureUploadException("client.timeout", "Timed out.", transient: true));

            await controller.SyncAsync(CancellationToken.None);
            var retried = await controller.RetryAsync(capture.Id, CancellationToken.None);

            Assert.IsNotNull(retried);
            Assert.AreEqual(CaptureUploadState.Queued, retried.State);
            Assert.IsNull(retried.FailureCode);

            await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(2, api.VoiceUploads.Count);
            Assert.AreEqual(
                api.VoiceUploads[0].IdempotencyKey,
                api.VoiceUploads[1].IdempotencyKey,
                "Both attempts must present the same key so the server can recognise the replay.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task An_upload_the_server_already_stored_counts_as_a_success()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            api.ReportAlreadyStored = true;
            var capture = await controller.SaveTextCaptureAsync("一句话。", CancellationToken.None);

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(1, outcome.Uploaded);
            Assert.AreEqual(0, (await controller.GetQueueAsync(CancellationToken.None)).Count);
            Assert.IsNull(await controller.FindAsync(capture.Id, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task One_unsendable_capture_does_not_block_the_others()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            await controller.SaveTextCaptureAsync("第一条。", CancellationToken.None);
            await controller.SaveTextCaptureAsync("第二条。", CancellationToken.None);

            // The first attempt fails permanently; the second must still go through.
            api.Failures.Enqueue(new CaptureUploadException("request.invalid", "Rejected.", transient: false));

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(1, outcome.Uploaded);
            Assert.AreEqual(1, outcome.Failed);
            Assert.AreEqual(1, (await controller.GetQueueAsync(CancellationToken.None)).Count);
            Assert.AreEqual(2, api.TextUploads.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Discarding_removes_the_capture_and_its_audio()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);

            await controller.DiscardAsync(capture.Id, CancellationToken.None);

            Assert.IsNull(await controller.FindAsync(capture.Id, CancellationToken.None));
            Assert.IsFalse(File.Exists(capture.LocalAudioPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task An_empty_queue_syncs_without_calling_the_server()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(0, outcome.Uploaded);
            Assert.AreEqual(0, api.VoiceUploads.Count);
            Assert.AreEqual(0, api.TextUploads.Count);
            Assert.IsFalse(outcome.AnythingHappened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_capture_keeps_the_capture_instant_even_when_it_is_uploaded_much_later()
    {
        var root = NewRoot();

        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 3, 1, 15, 50, 0, TimeSpan.Zero), localOffsetMinutes: -300);
            var (controller, _, api) = Arrange(root, clock);

            var capture = await controller.SaveTextCaptureAsync("当时想到的。", CancellationToken.None);

            // Four days later the connection comes back.
            clock.UtcNow = new DateTimeOffset(2026, 3, 5, 9, 0, 0, TimeSpan.Zero);
            await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(capture.CreatedAtUtc, api.TextUploads[0].CreatedAtUtc, "The capture instant never drifts.");
            Assert.AreEqual(-300, api.TextUploads[0].CreatedOffsetMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
