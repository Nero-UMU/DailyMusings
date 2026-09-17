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

    /// <summary>One entry as the server returns it, for the calls that answer with an entry rather than an envelope.</summary>
    private const string InputBody = """
        {"id":"server-1","sourceType":"voice","contentDate":"2026-03-01","createdAtUtc":"2026-03-01T15:50:00Z",
        "createdOffsetMinutes":480,"originalTranscript":"原来的转写。","revisedTranscript":null,
        "transcript":"原来的转写。","transcriptionStatus":"succeeded","failureCode":null,"hasAudio":true,
        "audioContentType":"audio/mp4","audioDurationSeconds":12.5,"isDeleted":false,
        "transcriptionJobStatus":"succeeded","transcriptionJobAttempts":1,"primaryTopicId":null,"secondaryTopicIds":[]}
        """;

    private static string TempAudio(string content = "audio-bytes")
    {
        var path = Path.Combine(Path.GetTempPath(), $"dm-audio-{Guid.CreateVersion7():N}.m4a");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Reads the single request body the stub recorded back into the contract type it came from.</summary>
    private static T Read<T>(StubHttpHandler handler) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            handler.Bodies.Single(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

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

    /// <summary>
    /// The read the Today screen makes while it appears. Sounding the same as an empty day when the server is gone
    /// was the first half of the bug a real device found; the other half was that the exception reached an
    /// <c>async void</c> handler and killed the process.
    /// </summary>
    /// <summary>§4.1 修订转写: the correction travels, and the server keeps the original (§6.1).</summary>
    [TestMethod]
    public async Task Revising_a_transcript_sends_the_correction_to_the_entry()
    {
        const string revised = """
            {"id":"server-1","sourceType":"voice","contentDate":"2026-03-01","createdAtUtc":"2026-03-01T15:50:00Z",
            "createdOffsetMinutes":480,"originalTranscript":"原来的转写。","revisedTranscript":"改过的转写。",
            "transcript":"改过的转写。","transcriptionStatus":"succeeded","failureCode":null,"hasAudio":true,
            "audioContentType":"audio/mp4","audioDurationSeconds":12.5,"isDeleted":false,
            "transcriptionJobStatus":"succeeded","transcriptionJobAttempts":1,"primaryTopicId":null,"secondaryTopicIds":[]}
            """;

        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, revised);

        var result = await Client(handler).ReviseTranscriptAsync("server-1", "改过的转写。", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("改过的转写。", result.Value!.RevisedTranscript);
        Assert.AreEqual("原来的转写。", result.Value.OriginalTranscript, "The original is never overwritten.");

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/api/inputs/server-1", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("改过的转写。", Read<ReviseTranscriptRequest>(handler).RevisedTranscript);
    }

    [TestMethod]
    public async Task Retrying_a_transcription_posts_to_the_retry_route_and_keeps_a_refusal_as_a_code()
    {
        var okHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(InputBody, Encoding.UTF8, "application/json"),
        });

        var ok = await Client(okHandler).RetryTranscriptionAsync("server-1", CancellationToken.None);

        Assert.IsTrue(ok.Succeeded);
        Assert.AreEqual("server-1", ok.Value!.Id);

        var request = okHandler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/api/inputs/server-1/retry-transcription", request.RequestUri!.AbsolutePath);

        // A retry the server refuses — the transcription already succeeded, say — is an answer, not a transport failure.
        var refused = await Client(StubHttpHandler.AlwaysJson(
                HttpStatusCode.Conflict,
                """{"code":"transcription.not_retryable","message":"..."}"""))
            .RetryTranscriptionAsync("server-1", CancellationToken.None);

        Assert.IsTrue(refused.ServerReached);
        Assert.AreEqual("transcription.not_retryable", refused.FailureCode);
    }

    [TestMethod]
    public async Task Reading_a_day_reports_an_unreachable_server_instead_of_an_empty_one()
    {
        var client = Client(StubHttpHandler.Throwing(new HttpRequestException("connection refused")));

        var result = await client.GetInputsAsync("2026-03-01", CancellationToken.None);

        Assert.IsFalse(result.ServerReached);
        Assert.AreEqual("client.network_unreachable", result.FailureCode);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task Reading_a_day_asks_for_the_date_and_uses_the_device_token()
    {
        const string body = """
            {"items":[{"id":"server-1","sourceType":"text","contentDate":"2026-03-01",
            "createdAtUtc":"2026-03-01T15:50:00.0000000+00:00","createdOffsetMinutes":480,"originalTranscript":"一句话。",
            "revisedTranscript":null,"transcript":"一句话。","transcriptionStatus":"notApplicable","failureCode":null,
            "hasAudio":false,"audioContentType":null,"audioDurationSeconds":null,"isDeleted":false,
            "transcriptionJobStatus":null,"transcriptionJobAttempts":0,"primaryTopicId":null,"secondaryTopicIds":[]}]}
            """;

        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, body);
        var client = Client(handler);

        var result = await client.GetInputsAsync("2026-03-01", CancellationToken.None);

        Assert.IsTrue(result.ServerReached);
        Assert.IsNull(result.FailureCode);
        Assert.AreEqual(1, result.Value!.Count);
        Assert.AreEqual("一句话。", result.Value![0].Transcript);

        var request = handler.Requests.Single();
        Assert.AreEqual("/api/inputs", request.RequestUri!.AbsolutePath);
        StringAssert.Contains(request.RequestUri.Query, "date=2026-03-01");
        Assert.AreEqual("device-token", request.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task Reading_a_day_says_so_when_the_device_token_was_rejected_or_the_answer_was_unreadable()
    {
        var rejected = await Client(StubHttpHandler.AlwaysStatus(HttpStatusCode.Unauthorized))
            .GetInputsAsync("2026-03-01", CancellationToken.None);

        // The server answered, so this is not "unreachable": the user has to pair the device again.
        Assert.IsTrue(rejected.ServerReached);
        Assert.AreEqual("auth.device_token_rejected", rejected.FailureCode);

        var unreadable = await Client(StubHttpHandler.AlwaysJson(HttpStatusCode.OK, "this is not json"))
            .GetInputsAsync("2026-03-01", CancellationToken.None);

        Assert.AreEqual("client.malformed_response", unreadable.FailureCode);

        var broken = await Client(StubHttpHandler.AlwaysStatus(HttpStatusCode.InternalServerError))
            .GetInputsAsync("2026-03-01", CancellationToken.None);

        Assert.AreEqual("server.rejected.500", broken.FailureCode);
    }

    [TestMethod]
    public async Task Reading_a_day_never_sends_a_request_from_an_unpaired_device()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, "{}");
        var client = Client(handler, token: null);

        var result = await client.GetInputsAsync("2026-03-01", CancellationToken.None);

        Assert.IsFalse(result.ServerReached);
        Assert.AreEqual("client.not_paired", result.FailureCode);
        Assert.AreEqual(0, handler.Requests.Count);
    }
}
