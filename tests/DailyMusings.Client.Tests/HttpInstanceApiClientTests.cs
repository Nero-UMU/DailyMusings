using System.Net;
using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Http;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The two reads the capture screen makes about the instance itself: an entry's stored recording (§15.2 step 6,
/// decision A.1) and the notification preferences (§9.3, §12). Both are reads the screen performs while the user is
/// looking at it, so neither may throw, and both must keep "the server said no" apart from "nothing came back".
/// </summary>
[TestClass]
public class HttpInstanceApiClientTests
{
    private const string InputBody = """
        {"id":"input-1","sourceType":"voice","contentDate":"2026-03-01","createdAtUtc":"2026-03-01T15:50:00Z",
        "createdOffsetMinutes":480,"originalTranscript":"原来的转写。","revisedTranscript":null,
        "transcript":"原来的转写。","transcriptionStatus":"succeeded","failureCode":null,"hasAudio":true,
        "audioContentType":"audio/mp4","audioDurationSeconds":12.5,"isDeleted":false,
        "transcriptionJobStatus":"succeeded","transcriptionJobAttempts":1,"primaryTopicId":null,"secondaryTopicIds":[]}
        """;

    private static HttpInstanceApiClient Client(StubHttpHandler handler, string? token = "device-token") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") }, new StubTokenProvider(token));

    [TestMethod]
    public async Task Fetching_a_recording_keeps_the_bytes_and_the_content_type()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mp4");
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await Client(handler).GetAudioAsync("input-1", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(bytes, result.Value!.Bytes);
        Assert.AreEqual("audio/mp4", result.Value.ContentType);
        Assert.AreEqual(".m4a", result.Value.FileExtension);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("/api/inputs/input-1/audio", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("device-token", request.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task A_recording_that_retention_already_removed_comes_back_as_the_servers_code()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.NotFound,
            """{"code":"input.audio.deleted","message":"..."}""");

        var result = await Client(handler).GetAudioAsync("input-1", CancellationToken.None);

        Assert.IsTrue(result.ServerReached);
        Assert.AreEqual("input.audio.deleted", result.FailureCode);
    }

    [TestMethod]
    public async Task An_empty_recording_is_not_treated_as_a_playable_clip()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        });

        var result = await Client(handler).GetAudioAsync("input-1", CancellationToken.None);

        Assert.AreEqual("client.empty_response", result.FailureCode);
    }

    [TestMethod]
    public async Task Notification_preferences_are_read_with_the_device_token()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.OK,
            """
            {"smtpConfigured":true,"toAddress":"owner@example.test","instanceUrl":"http://127.0.0.1:8080",
            "draftReady":true,"jobFailed":false,"automaticPublication":false}
            """);

        var result = await Client(handler).GetNotificationSettingsAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("owner@example.test", result.Value!.ToAddress);
        Assert.IsTrue(result.Value.DraftReady);

        Assert.AreEqual("/api/notification-settings", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task A_device_that_may_not_read_them_is_told_that_the_administrator_can()
    {
        var client = Client(StubHttpHandler.AlwaysStatus(HttpStatusCode.Forbidden));

        var result = await client.GetNotificationSettingsAsync(CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("auth.forbidden", result.FailureCode, "The settings page turns this into 'change it on the admin page'.");
    }

    [TestMethod]
    public async Task Neither_read_leaves_the_device_before_it_is_paired()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, InputBody);
        var client = Client(handler, token: null);

        Assert.AreEqual("client.not_paired", (await client.GetAudioAsync("input-1", CancellationToken.None)).FailureCode);
        Assert.AreEqual("client.not_paired", (await client.GetNotificationSettingsAsync(CancellationToken.None)).FailureCode);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task A_clip_is_written_to_a_cache_file_a_player_can_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-playback", Guid.CreateVersion7().ToString("N"));

        try
        {
            var cache = new AudioClipCache(root);
            var clip = new AudioClip(Encoding.UTF8.GetBytes("audio-bytes"), "audio/mp4");

            var path = await cache.SaveAsync("01a0ad63-3b66-7d56-af86-956f3c39f7f3", clip, CancellationToken.None);

            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(".m4a", Path.GetExtension(path));
            CollectionAssert.AreEqual(clip.Bytes, await File.ReadAllBytesAsync(path));

            // Path separators and dots never reach the file name: the id came from the server, and a player must be
            // handed a path this app chose.
            Assert.IsFalse(Path.GetFileName(path).Contains("..", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
