using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Core.Offline;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The capture façade (docs/开发指导.md §9.2, and the phone's 2026-09-24 scope). Two orderings carry the product's
/// promises, and both are asserted rather than assumed: the recording is durable before the UI may say "recorded",
/// and a successful upload keeps the local copy — the device is the source of truth for 往期记录, so nothing here
/// deletes a capture except the user asking for it.
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
            Assert.IsTrue(capture.NeedsUpload);
            Assert.IsTrue(capture.IsVoice);
            Assert.IsNull(capture.Transcript);
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
            Assert.AreEqual("随手记一句。", capture.DisplayText);
            Assert.IsNull(capture.LocalAudioPath);
            Assert.AreEqual(0, Directory.EnumerateFiles(store.AudioDirectory).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The behaviour this round changed: the server confirming the capture no longer deletes the device's copy, and
    /// the text the server recognised lands in the local record so the calendar can show it without a server.
    /// </summary>
    [TestMethod]
    public async Task An_uploaded_capture_stays_on_the_device_with_its_transcript()
    {
        var root = NewRoot();

        try
        {
            var clock = new FakeClock();
            var (controller, _, api) = Arrange(root, clock);
            api.VoiceTranscript = "今天想到了一个办法。";

            var saved = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 6.1, CancellationToken.None);

            var outcome = await controller.UploadAsync(saved.Id, CancellationToken.None);

            Assert.IsTrue(outcome.Uploaded);
            Assert.IsNull(outcome.FailureCode);
            Assert.AreEqual("今天想到了一个办法。", outcome.Transcript);

            var kept = await controller.FindAsync(saved.Id, CancellationToken.None);

            Assert.IsNotNull(kept, "The local copy is what the calendar reads; a confirmed upload must not remove it.");
            Assert.AreEqual(CaptureUploadState.Uploaded, kept.State);
            Assert.IsFalse(kept.NeedsUpload);
            Assert.AreEqual("server-1", kept.ServerInputId);
            Assert.AreEqual("今天想到了一个办法。", kept.Transcript);
            Assert.AreEqual("今天想到了一个办法。", kept.DisplayText);
            Assert.AreEqual(clock.UtcNow, kept.UploadedAtUtc);
            Assert.AreEqual(clock.UtcNow, kept.TranscriptionAtUtc);
            Assert.IsTrue(File.Exists(kept.LocalAudioPath!), "The recording stays on the device after the upload.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A text capture's transcript is what the user typed, which is what the server stores too.</summary>
    [TestMethod]
    public async Task A_typed_capture_keeps_its_own_text_as_the_transcript()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);
            var saved = await controller.SaveTextCaptureAsync("手写的一句。", CancellationToken.None);

            var outcome = await controller.UploadAsync(saved.Id, CancellationToken.None);

            Assert.IsTrue(outcome.Uploaded);
            Assert.AreEqual("手写的一句。", outcome.Transcript);

            var kept = await controller.FindAsync(saved.Id, CancellationToken.None);
            Assert.AreEqual("手写的一句。", kept!.Transcript);
            Assert.AreEqual("手写的一句。", kept.DisplayText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An upload the server answered while the model was still working writes no text, and says so.</summary>
    [TestMethod]
    public async Task An_upload_still_being_transcribed_records_no_transcript_yet()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            api.VoiceTranscript = null;
            api.VoiceTranscriptionStatus = DailyMusings.Contracts.TranscriptionStatusNames.Pending;

            var saved = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 4, CancellationToken.None);
            var outcome = await controller.UploadAsync(saved.Id, CancellationToken.None);

            Assert.IsTrue(outcome.Uploaded);
            Assert.IsNull(outcome.Transcript);

            var kept = await controller.FindAsync(saved.Id, CancellationToken.None);

            Assert.AreEqual(CaptureUploadState.Uploaded, kept!.State);
            Assert.IsNull(kept.Transcript);
            Assert.IsNull(kept.TranscriptionAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The read that completes a pending transcription: the server has the text a moment later, and the local
    /// record — which is what the calendar shows — has to end up holding it.
    /// </summary>
    [TestMethod]
    public async Task Refreshing_from_the_server_fills_in_a_transcript_that_arrived_late()
    {
        var root = NewRoot();

        try
        {
            var clock = new FakeClock();
            var (controller, _, api) = Arrange(root, clock);
            api.VoiceTranscript = null;
            api.VoiceTranscriptionStatus = DailyMusings.Contracts.TranscriptionStatusNames.Pending;

            var saved = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 4, CancellationToken.None);
            await controller.UploadAsync(saved.Id, CancellationToken.None);

            // The model finishes, and the entry is now readable with its text.
            api.ServerInput = ServerEntry();

            var refreshed = await controller.RefreshFromServerAsync(saved.Id, CancellationToken.None);

            Assert.AreEqual("server-1", api.ReadInputIds.Single());
            Assert.AreEqual("后到的识别文字。", refreshed!.Transcript);
            Assert.AreEqual(clock.UtcNow, refreshed.TranscriptionAtUtc);

            var stored = await controller.FindAsync(saved.Id, CancellationToken.None);
            Assert.AreEqual("后到的识别文字。", stored!.Transcript);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Refreshing_never_throws_when_the_server_cannot_be_reached()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            api.VoiceTranscript = null;

            var saved = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 4, CancellationToken.None);
            await controller.UploadAsync(saved.Id, CancellationToken.None);

            api.Exceptions.Enqueue(new HttpRequestException("connection refused"));

            var refreshed = await controller.RefreshFromServerAsync(saved.Id, CancellationToken.None);

            // Nothing changed, nothing was lost, and the screen has nothing to report: the recording is safe here.
            Assert.IsNotNull(refreshed);
            Assert.IsNull(refreshed.Transcript);
            Assert.IsTrue(File.Exists(saved.LocalAudioPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_failed_upload_keeps_the_audio_and_records_a_visible_reason()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            api.Failures.Enqueue(new CaptureUploadException("client.network_unreachable", "No network.", transient: true));

            var outcome = await controller.UploadAsync(capture.Id, CancellationToken.None);

            Assert.IsFalse(outcome.Uploaded);
            Assert.AreEqual("client.network_unreachable", outcome.FailureCode);
            Assert.AreEqual("No network.", outcome.FailureSummary);

            var queued = await controller.FindAsync(capture.Id, CancellationToken.None);

            Assert.IsNotNull(queued);
            Assert.AreEqual(CaptureUploadState.Failed, queued.State);
            Assert.AreEqual("client.network_unreachable", queued.FailureCode);
            Assert.AreEqual("No network.", queued.FailureSummary);
            Assert.AreEqual(1, queued.AttemptCount);
            Assert.IsTrue(queued.NeedsUpload, "A failed upload is still waiting to go out.");
            Assert.IsTrue(File.Exists(queued.LocalAudioPath!), "§20: a failing model endpoint must not lose the input.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Uploading_a_capture_that_is_not_on_the_device_reports_a_code_instead_of_throwing()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);

            var outcome = await controller.UploadAsync("no-such-capture", CancellationToken.None);

            Assert.IsFalse(outcome.Uploaded);
            Assert.AreEqual("client.capture_missing", outcome.FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The dedupe guarantee: a second attempt must reuse the key, or the server would create a second entry.</summary>
    [TestMethod]
    public async Task A_second_attempt_reuses_the_same_idempotency_key()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            api.Failures.Enqueue(new CaptureUploadException("client.timeout", "Timed out.", transient: true));

            await controller.UploadAsync(capture.Id, CancellationToken.None);

            // A failed row is still outstanding, so the next sync picks it up again without the user doing anything.
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
    public async Task Syncing_uploads_each_outstanding_capture_once_and_keeps_every_local_copy()
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
            Assert.AreEqual(0, outcome.StillQueued, "Everything the server confirmed has stopped waiting.");
            Assert.AreEqual(1, api.VoiceUploads.Count);
            Assert.AreEqual(1, api.TextUploads.Count);

            // Both the record and the audio are still here: uploading is not a reason to delete anything.
            var local = await controller.ListLocalAsync(CancellationToken.None);
            Assert.AreEqual(2, local.Count);
            Assert.IsTrue(local.All(capture => capture.State == CaptureUploadState.Uploaded));
            Assert.IsTrue(File.Exists(voice.LocalAudioPath!));
            Assert.AreEqual(1, Directory.EnumerateFiles(store.AudioDirectory).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Thoughts should reach the server in the order they were had, whatever order the archive lists them.</summary>
    [TestMethod]
    public async Task Syncing_uploads_the_oldest_thought_first()
    {
        var root = NewRoot();

        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
            var (controller, _, api) = Arrange(root, clock);

            await controller.SaveTextCaptureAsync("早上想到的。", CancellationToken.None);
            clock.UtcNow = clock.UtcNow.AddHours(6);
            await controller.SaveTextCaptureAsync("下午想到的。", CancellationToken.None);

            await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual("早上想到的。", api.TextUploads[0].Text);
            Assert.AreEqual("下午想到的。", api.TextUploads[1].Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The ordering test: the device must still hold its copy while the server is receiving it.</summary>
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
            Assert.AreEqual(0, outcome.Failed);

            var kept = await controller.FindAsync(capture.Id, CancellationToken.None);
            Assert.AreEqual(CaptureUploadState.Uploaded, kept!.State);
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
            Assert.AreEqual(1, outcome.StillQueued);
            Assert.AreEqual(2, api.TextUploads.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Found on a real phone with the network path blackholed rather than refused: the HTTP stack surfaced a bare
    /// "Canceled" that was not a <see cref="CaptureUploadException"/>, so it escaped the sync loop, and the screen
    /// reported it as an operation failure while the queue still claimed the capture was waiting to upload. A capture
    /// that is already durable on disk must always come out of a failed attempt as a retryable failure.
    /// </summary>
    [TestMethod]
    public async Task An_unclassified_upload_failure_becomes_a_retryable_row_instead_of_escaping()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, api) = Arrange(root);
            var capture = await controller.SaveTextCaptureAsync("网络黑洞里写的一句。", CancellationToken.None);

            api.Exceptions.Enqueue(new TaskCanceledException("Canceled"));

            var outcome = await controller.SyncAsync(CancellationToken.None);

            Assert.AreEqual(0, outcome.Uploaded);
            Assert.AreEqual(1, outcome.Failed);
            Assert.AreEqual(1, outcome.StillQueued);

            var queued = await controller.FindAsync(capture.Id, CancellationToken.None);
            Assert.IsNotNull(queued, "The capture must still be on the device.");
            Assert.AreEqual(CaptureUploadState.Failed, queued.State);
            Assert.AreEqual("client.upload_failed", queued.FailureCode);
            Assert.AreEqual(1, queued.AttemptCount);
            Assert.AreEqual("Canceled", queued.FailureSummary);
            Assert.AreEqual(capture.IdempotencyKey, queued.IdempotencyKey);

            // And the next sync tries again with the same key, without the user having to do anything.
            var second = await controller.SyncAsync(CancellationToken.None);
            Assert.AreEqual(1, second.Uploaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task The_archive_lists_the_newest_capture_first()
    {
        var root = NewRoot();

        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
            var (controller, _, _) = Arrange(root, clock);

            var early = await controller.SaveTextCaptureAsync("早上。", CancellationToken.None);
            clock.UtcNow = clock.UtcNow.AddHours(9);
            var late = await controller.SaveTextCaptureAsync("傍晚。", CancellationToken.None);

            var local = await controller.ListLocalAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { late.Id, early.Id },
                local.Select(capture => capture.Id).ToArray(),
                "The calendar reads this list, and the thoughts a person wants back are the recent ones.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Deleting_a_local_capture_removes_the_record_and_its_audio()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);
            var capture = await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);

            var deleted = await controller.DeleteLocalAsync(capture.Id, CancellationToken.None);

            Assert.IsNotNull(deleted);
            Assert.AreEqual(capture.Id, deleted.Id);
            Assert.IsNull(await controller.FindAsync(capture.Id, CancellationToken.None));
            Assert.IsFalse(File.Exists(capture.LocalAudioPath!));
            Assert.AreEqual(0, (await controller.ListLocalAsync(CancellationToken.None)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Clearing_the_archive_removes_every_record_and_recording()
    {
        var root = NewRoot();

        try
        {
            var (controller, store, _) = Arrange(root);
            await controller.SaveVoiceCaptureAsync(Bytes("audio"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            await controller.SaveTextCaptureAsync("一句话。", CancellationToken.None);

            var removed = await controller.ClearLocalAsync(CancellationToken.None);

            Assert.AreEqual(2, removed);
            Assert.AreEqual(0, (await controller.ListLocalAsync(CancellationToken.None)).Count);
            Assert.AreEqual(0, Directory.EnumerateFiles(store.AudioDirectory).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task The_usage_read_counts_records_recordings_and_their_bytes()
    {
        var root = NewRoot();

        try
        {
            var (controller, _, _) = Arrange(root);
            await controller.SaveVoiceCaptureAsync(Bytes("12345"), ".m4a", "audio/mp4", 3, CancellationToken.None);
            await controller.SaveTextCaptureAsync("一句话。", CancellationToken.None);

            var usage = await controller.GetLocalUsageAsync(CancellationToken.None);

            Assert.AreEqual(2, usage.Count);
            Assert.AreEqual(1, usage.VoiceCount);
            Assert.AreEqual(5, usage.AudioBytes);
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

    /// <summary>
    /// Builds an entry the way the server would answer <c>GET /api/inputs/{id}</c> once recognition has finished.
    /// </summary>
    private static InputDto ServerEntry() => new(
        "server-1",
        InputSourceNames.Voice,
        "2026-03-01",
        "2026-03-01T15:50:00.0000000+00:00",
        480,
        "后到的识别文字。",
        null,
        "后到的识别文字。",
        TranscriptionStatusNames.Succeeded,
        null,
        true,
        "audio/mp4",
        12.5,
        false,
        JobStatusNames.Succeeded,
        1,
        null,
        []);
}
