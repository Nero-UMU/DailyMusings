using System.Net;
using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Http;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The HTTP half of the client (docs/开发指导.md §8.2, §13). Its real job is classification: a retryable failure
/// leaves as transient, a failure that needs the user leaves as permanent, and the idempotency key always travels.
/// </summary>
[TestClass]
public class HttpCaptureApiClientTests
{
    private const string SuccessBody = """
        {"alreadyStored":false,"input":{"id":"server-1","sourceType":"voice","contentDate":"2026-03-01",
        "createdAtUtc":"2026-03-01T15:50:00.0000000+00:00","createdOffsetMinutes":480,"originalTranscript":null,
        "revisedTranscript":null,"transcript":null,"transcriptionStatus":"pending","failureCode":null,
        "hasAudio":true,"audioContentType":"audio/mp4","audioDurationSeconds":12.5,"isDeleted":false,
        "transcriptionJobStatus":"pending","transcriptionJobAttempts":0}}
        """;

    private static string TempAudio(string content = "audio-bytes")
    {
        var path = Path.Combine(Path.GetTempPath(), $"dm-audio-{Guid.CreateVersion7():N}.m4a");
        File.WriteAllText(path, content);
        return path;
    }

    private static HttpCaptureApiClient Client(
        StubHttpHandler handler,
        string? token = "device-token",
        Action<HttpClient>? configure = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        configure?.Invoke(http);

        return new HttpCaptureApiClient(http, new StubTokenProvider(token));
    }

    private static VoiceUpload Voice(string path, string idempotencyKey = "key-1") => new(
        path,
        "audio/mp4",
        12.5,
        idempotencyKey,
        new DateTimeOffset(2026, 3, 1, 15, 50, 0, TimeSpan.Zero),
        480);

    [TestMethod]
    public async Task A_voice_upload_carries_the_capture_instant_the_offset_and_the_idempotency_key()
    {
        var audio = TempAudio();

        try
        {
            var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, SuccessBody);
            var client = Client(handler);

            var response = await client.UploadVoiceAsync(Voice(audio), CancellationToken.None);

            Assert.IsNotNull(response);
            Assert.IsFalse(response.AlreadyStored);

            var request = handler.Requests.Single();
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/inputs/voice", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("device-token", request.Headers.Authorization.Parameter);

            var body = handler.Bodies.Single();
            StringAssert.Contains(body, $"name={VoiceUploadFields.Audio}");
            StringAssert.Contains(body, $"name={VoiceUploadFields.IdempotencyKey}");
            StringAssert.Contains(body, "key-1");
            StringAssert.Contains(body, $"name={VoiceUploadFields.CreatedOffsetMinutes}");
            StringAssert.Contains(body, "480");
            StringAssert.Contains(body, "audio-bytes");
        }
        finally
        {
            File.Delete(audio);
        }
    }

    [TestMethod]
    public async Task A_text_upload_uses_the_shared_field_names_and_the_same_idempotency_key()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, SuccessBody);
        var client = Client(handler);

        await client.UploadTextAsync(
            new TextUpload("一句话。", "key-text", new DateTimeOffset(2026, 3, 1, 15, 50, 0, TimeSpan.Zero), 480),
            CancellationToken.None);

        var body = handler.Bodies.Single();
        StringAssert.Contains(body, "key-text");
        StringAssert.Contains(body, "480");
        StringAssert.Contains(body, "\"text\"");
    }

    [TestMethod]
    public async Task A_rejected_device_token_is_never_retried()
    {
        var audio = TempAudio();

        try
        {
            var handler = StubHttpHandler.AlwaysJson(
                HttpStatusCode.Unauthorized,
                """{"code":"auth.unauthenticated","message":"Authentication is required."}""");

            var client = Client(handler);

            var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
                client.UploadVoiceAsync(Voice(audio), CancellationToken.None));

            Assert.IsFalse(failure.Transient, "Retrying a rejected token forever would hide that re-pairing is needed.");
            Assert.AreEqual("auth.unauthenticated", failure.Code);
        }
        finally
        {
            File.Delete(audio);
        }
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.TooManyRequests, true)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    [DataRow(HttpStatusCode.RequestEntityTooLarge, false)]
    public async Task Server_failures_are_classified_by_whether_waiting_could_help(
        HttpStatusCode statusCode,
        bool expectedTransient)
    {
        var audio = TempAudio();

        try
        {
            var client = Client(StubHttpHandler.AlwaysJson(statusCode, """{"code":"server.rejected","message":"no"}"""));

            var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
                client.UploadVoiceAsync(Voice(audio), CancellationToken.None));

            Assert.AreEqual(expectedTransient, failure.Transient, $"status {statusCode}");
            Assert.AreEqual("server.rejected", failure.Code, "The server's own code must survive.");
        }
        finally
        {
            File.Delete(audio);
        }
    }

    [TestMethod]
    public async Task An_unreachable_server_is_transient()
    {
        var audio = TempAudio();

        try
        {
            var client = Client(StubHttpHandler.Throwing(new HttpRequestException("no route to host")));

            var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
                client.UploadVoiceAsync(Voice(audio), CancellationToken.None));

            Assert.IsTrue(failure.Transient);
            Assert.AreEqual("client.network_unreachable", failure.Code);
        }
        finally
        {
            File.Delete(audio);
        }
    }

    [TestMethod]
    public async Task An_unpaired_device_is_refused_before_any_request_is_sent()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, SuccessBody);
        var client = Client(handler, token: null);

        var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
            client.UploadTextAsync(
                new TextUpload("一句话。", "key", DateTimeOffset.UnixEpoch, 0),
                CancellationToken.None));

        Assert.IsFalse(failure.Transient);
        Assert.AreEqual("client.not_paired", failure.Code);
        Assert.AreEqual(0, handler.Requests.Count, "Nothing may leave the device before it is paired.");
    }

    [TestMethod]
    public async Task A_missing_local_recording_fails_permanently_instead_of_being_retried_forever()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, SuccessBody);
        var client = Client(handler);

        var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
            client.UploadVoiceAsync(Voice("/nonexistent/recording.m4a"), CancellationToken.None));

        Assert.IsFalse(failure.Transient);
        Assert.AreEqual("client.audio_missing", failure.Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task An_empty_response_body_is_treated_as_transient_rather_than_as_success()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, "null");
        var client = Client(handler);

        var failure = await Assert.ThrowsExceptionAsync<CaptureUploadException>(() =>
            client.UploadTextAsync(
                new TextUpload("一句话。", "key", DateTimeOffset.UnixEpoch, 0),
                CancellationToken.None));

        Assert.IsTrue(failure.Transient);
        Assert.AreEqual("client.empty_response", failure.Code);
    }

    [TestMethod]
    public async Task The_content_type_is_derived_from_the_local_file_extension()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, SuccessBody);
        var client = Client(handler);
        var path = Path.Combine(Path.GetTempPath(), $"dm-audio-{Guid.CreateVersion7():N}.m4a");
        await File.WriteAllTextAsync(path, "x");

        try
        {
            await client.UploadVoiceAsync(Voice(path), CancellationToken.None);

            var body = handler.Bodies.Single();
            StringAssert.Contains(body, "audio/mp4");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
