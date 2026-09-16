using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The capture loop end to end: a paired device uploads audio, the executor picks up the queued job, the
/// transcription endpoint is called, and the result reaches the entry — over real HTTP, against a controllable
/// stand-in for the model endpoint (docs/开发指导.md §8.2, §9.2).
/// </summary>
[TestClass]
[DoNotParallelize]
public class InputLoopTests
{
    private static readonly byte[] FakeAudio = "not-really-m4a-but-the-stub-does-not-care"u8.ToArray();

    private static Dictionary<string, string?> TranscriptionEnabled(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["Transcription:Enabled"] = "true",
        ["Transcription:BaseUrl"] = baseUrl,
        ["Transcription:Model"] = "test-whisper",
        ["Transcription:SecretName"] = "openai-api-key",
        ["Transcription:TimeoutSeconds"] = "30",
    };

    [TestMethod]
    public async Task A_voice_capture_is_transcribed_end_to_end()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "capture-1");
        upload.EnsureSuccessStatusCode();

        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.IsNotNull(ingested);
        Assert.IsFalse(ingested.AlreadyStored);
        Assert.AreEqual(TranscriptionStatusNames.Pending, ingested.Input.TranscriptionStatus);

        var transcribed = await WaitForTranscriptionAsync(instance, device, ingested.Input.Id);

        Assert.AreEqual(TranscriptionStatusNames.Succeeded, transcribed.TranscriptionStatus);
        Assert.AreEqual(stub.State.ResponseText, transcribed.Transcript);
        Assert.AreEqual(JobStatusNames.Succeeded, transcribed.TranscriptionJobStatus);

        // The audio really arrived at the endpoint, with the configured model and the secret.
        Assert.AreEqual(1, stub.State.RequestCount);
        Assert.AreEqual("test-whisper", stub.State.LastModel);
        Assert.AreEqual("Bearer test-api-key", stub.State.LastAuthorization);
        Assert.AreEqual(FakeAudio.Length, stub.State.LastAudioBytes);
        Assert.AreEqual("audio/mp4", stub.State.LastContentType);

        // And it is still stored locally, because the entry keeps its audio until retention says otherwise.
        Assert.IsTrue(transcribed.HasAudio);
        var mediaDirectory = Path.Combine(instance.RootPath, "media");
        Assert.AreEqual(
            1,
            CountMediaFiles(instance),
            "The uploaded blob must be on the media volume. Found: " + DescribeMediaFiles(instance));
    }

    [TestMethod]
    public async Task A_capture_carries_the_devices_content_day_and_its_capture_instant()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        // Captured late on 2026-03-01 in +08:00, which is 15:50 UTC — the content day is that same day.
        using var upload = await UploadVoiceAsync(
            device,
            "capture-content-day",
            createdAtUtc: "2026-03-01T15:50:00.0000000+00:00",
            createdOffsetMinutes: 480);
        upload.EnsureSuccessStatusCode();

        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

        Assert.AreEqual("2026-03-01", ingested!.Input.ContentDate);
        Assert.AreEqual(480, ingested.Input.CreatedOffsetMinutes);

        // The day the client asked for is the day it can list by.
        var listed = await device.GetFromJsonAsync<InputListResponse>("/api/inputs?date=2026-03-01");
        Assert.AreEqual(1, listed!.Items.Count);
    }

    [TestMethod]
    public async Task A_replayed_upload_returns_the_original_entry_instead_of_a_duplicate()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var first = await UploadVoiceAsync(device, "same-key");
        var firstBody = await first.Content.ReadFromJsonAsync<IngestResponse>();

        // The client never learned whether the first attempt landed, so it retries with the same key.
        using var second = await UploadVoiceAsync(device, "same-key");
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<IngestResponse>();

        Assert.IsTrue(secondBody!.AlreadyStored, "A replayed upload must report that it was already stored.");
        Assert.AreEqual(firstBody!.Input.Id, secondBody.Input.Id);

        var listed = await device.GetFromJsonAsync<InputListResponse>("/api/inputs");
        Assert.AreEqual(1, listed!.Items.Count, "A retried upload must not produce a second entry.");

        Assert.AreEqual(
            1,
            CountMediaFiles(instance),
            "A replayed upload must not write a second blob. Found: " + DescribeMediaFiles(instance));
    }

    [TestMethod]
    public async Task A_text_capture_needs_no_transcription()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var response = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("随手记一句。", "2026-03-01T15:50:00.0000000+00:00", 480, "text-1"));

        response.EnsureSuccessStatusCode();
        var ingested = await response.Content.ReadFromJsonAsync<IngestResponse>();

        Assert.AreEqual(InputSourceNames.Text, ingested!.Input.SourceType);
        Assert.AreEqual(TranscriptionStatusNames.NotApplicable, ingested.Input.TranscriptionStatus);
        Assert.AreEqual("随手记一句。", ingested.Input.Transcript);
        Assert.IsNull(ingested.Input.TranscriptionJobStatus, "There is nothing to transcribe.");
    }

    [TestMethod]
    public async Task An_unconfigured_endpoint_fails_the_capture_in_a_way_the_client_can_explain()
    {
        // Transcription is off by default, so nothing is ever sent anywhere by accident (§8.1).
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "unconfigured");
        upload.EnsureSuccessStatusCode();
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

        var settled = await WaitForAsync(
            device,
            ingested!.Input.Id,
            view => view.TranscriptionStatus == TranscriptionStatusNames.Failed);

        Assert.AreEqual("transcription.disabled", settled.FailureCode);
        Assert.AreEqual(JobStatusNames.Failed, settled.TranscriptionJobStatus);

        // The audio is kept, so configuring the endpoint later makes the capture recoverable (§20).
        Assert.IsTrue(settled.HasAudio);
    }

    [TestMethod]
    public async Task A_transient_endpoint_failure_backs_off_instead_of_giving_up()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        stub.State.FailWithStatusCode = HttpStatusCode.ServiceUnavailable;

        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));
        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "transient");
        upload.EnsureSuccessStatusCode();
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

        var afterFirstAttempt = await WaitForAsync(
            device,
            ingested!.Input.Id,
            view => view.TranscriptionJobAttempts >= 1 && view.TranscriptionStatus == TranscriptionStatusNames.Failed);

        // A 5xx is retryable: the job stays queued with a backoff rather than failing for good.
        Assert.AreEqual(JobStatusNames.Pending, afterFirstAttempt.TranscriptionJobStatus);
        Assert.AreEqual(1, afterFirstAttempt.TranscriptionJobAttempts);
        Assert.AreEqual("transcription.upstream_unavailable", afterFirstAttempt.FailureCode);

        // Once the endpoint recovers, the retry succeeds without the user doing anything.
        stub.State.Reset();

        var recovered = await WaitForAsync(
            device,
            ingested.Input.Id,
            view => view.TranscriptionStatus is TranscriptionStatusNames.Succeeded,
            timeout: TimeSpan.FromSeconds(90));

        Assert.AreEqual(stub.State.ResponseText, recovered.Transcript);
    }

    /// <summary>
    /// Regression for a defect the real pipeline exposed: a provider answering with an unexpected shape used to
    /// escape as an unclassified exception, leaving the entry stuck in progress and un-retryable.
    /// </summary>
    [TestMethod]
    public async Task An_endpoint_answering_with_an_unreadable_shape_leaves_the_entry_retryable()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        stub.State.ResponseBodyOverride = """{"text":["an array where a string belongs"]}""";

        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));
        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "malformed-response");
        upload.EnsureSuccessStatusCode();
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

        var failed = await WaitForAsync(
            device,
            ingested!.Input.Id,
            view => view.TranscriptionJobStatus == JobStatusNames.Failed);

        Assert.AreEqual("transcription.malformed_response", failed.FailureCode);
        Assert.AreEqual(
            TranscriptionStatusNames.Failed,
            failed.TranscriptionStatus,
            "An unreadable answer must not leave the entry stuck in progress.");

        // And the user can recover it once the endpoint behaves again.
        stub.State.Reset();

        using var retry = await device.PostAsync($"/api/inputs/{ingested.Input.Id}/retry-transcription", null);
        retry.EnsureSuccessStatusCode();

        var recovered = await WaitForAsync(
            device,
            ingested.Input.Id,
            view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded);

        Assert.AreEqual(stub.State.ResponseText, recovered.Transcript);
    }

    [TestMethod]
    public async Task A_rejected_credential_fails_terminally_and_can_be_retried_once_fixed()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        stub.State.FailWithStatusCode = HttpStatusCode.Unauthorized;

        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));
        instance.WriteSecret("openai-api-key", "wrong-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "bad-credentials");
        upload.EnsureSuccessStatusCode();
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

        var failed = await WaitForAsync(
            device,
            ingested!.Input.Id,
            view => view.TranscriptionJobStatus == JobStatusNames.Failed);

        // A 401 cannot be fixed by waiting, so burning three attempts would only delay the news.
        Assert.AreEqual(1, failed.TranscriptionJobAttempts);
        Assert.AreEqual("transcription.credentials_rejected", failed.FailureCode);

        // The operator fixes the secret, then asks for a retry.
        stub.State.Reset();

        using var retry = await device.PostAsync($"/api/inputs/{ingested.Input.Id}/retry-transcription", null);
        retry.EnsureSuccessStatusCode();

        var recovered = await WaitForAsync(
            device,
            ingested.Input.Id,
            view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded);

        Assert.AreEqual(stub.State.ResponseText, recovered.Transcript);
    }

    [TestMethod]
    public async Task The_stored_recording_can_be_read_back_until_retention_removes_it()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "play-me");
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();
        var id = ingested!.Input.Id;

        await WaitForAsync(device, id, view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded);

        // §15.2 step 6: the restored instance's audio has to be checkable, which needs a read path.
        using var audio = await device.GetAsync($"/api/inputs/{id}/audio");
        audio.EnsureSuccessStatusCode();

        Assert.AreEqual("audio/mp4", audio.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(FakeAudio, await audio.Content.ReadAsByteArrayAsync());

        // A player seeks, so the endpoint has to answer range requests.
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/inputs/{id}/audio");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);
        using var partial = await device.SendAsync(rangeRequest);

        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.AreEqual(10, (await partial.Content.ReadAsByteArrayAsync()).Length);

        // A caller with no credential is not allowed to listen to somebody's recording.
        using var anonymous = new HttpClient { BaseAddress = instance.Client.BaseAddress };
        using var forbidden = await anonymous.GetAsync($"/api/inputs/{id}/audio");
        Assert.AreEqual(HttpStatusCode.Unauthorized, forbidden.StatusCode);

        // Once retention (or the user) removes the blob, the entry stays and the read answers honestly.
        using var deleteAudio = await device.DeleteAsync($"/api/inputs/{id}/audio");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteAudio.StatusCode);

        using var gone = await device.GetAsync($"/api/inputs/{id}/audio");
        Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.AreEqual("input.audio.deleted", (await gone.Content.ReadFromJsonAsync<ApiError>())?.Code);

        using var stillThere = await device.GetAsync($"/api/inputs/{id}");
        stillThere.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task A_revision_is_kept_apart_from_the_original_and_audio_deletion_keeps_the_entry()
    {
        await using var stub = await StubTranscriptionEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(TranscriptionEnabled(stub.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var upload = await UploadVoiceAsync(device, "revise-me");
        var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();
        var id = ingested!.Input.Id;

        await WaitForAsync(device, id, view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded);

        using var revise = await device.PatchAsJsonAsync(
            $"/api/inputs/{id}",
            new ReviseTranscriptRequest("用户修订后的文字。"));

        revise.EnsureSuccessStatusCode();
        var revised = await revise.Content.ReadFromJsonAsync<InputDto>();

        Assert.AreEqual("用户修订后的文字。", revised!.RevisedTranscript);
        Assert.AreEqual(stub.State.ResponseText, revised.OriginalTranscript, "The original is never overwritten.");
        Assert.AreEqual("用户修订后的文字。", revised.Transcript, "Generation uses the revision.");

        // Deleting the audio kills the blob but not the record (§17.1).
        using var deleteAudio = await device.DeleteAsync($"/api/inputs/{id}/audio");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteAudio.StatusCode);

        using var afterAudioDelete = await device.GetAsync($"/api/inputs/{id}");
        var kept = await afterAudioDelete.Content.ReadFromJsonAsync<InputDto>();

        Assert.IsFalse(kept!.HasAudio);
        Assert.IsFalse(kept.IsDeleted);
        Assert.AreEqual("用户修订后的文字。", kept.Transcript);

        Assert.AreEqual(
            0,
            CountMediaFiles(instance),
            "Deleting the audio must remove it from the volume. Remaining: " + DescribeMediaFiles(instance));

        // Deleting the entry is a different operation, and it takes the record with it.
        using var deleteEntry = await device.DeleteAsync($"/api/inputs/{id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteEntry.StatusCode);

        var listed = await device.GetFromJsonAsync<InputListResponse>("/api/inputs");
        Assert.AreEqual(0, listed!.Items.Count);
    }

    [TestMethod]
    public async Task An_unauthenticated_caller_cannot_touch_captures()
    {
        await using var instance = await TestInstance.StartAsync();

        using var uploaded = await UploadVoiceAsync(new HttpClient { BaseAddress = instance.Client.BaseAddress }, "anon");
        Assert.AreEqual(HttpStatusCode.Unauthorized, uploaded.StatusCode);

        using var listed = await instance.Client.GetAsync("/api/inputs");
        Assert.AreEqual(HttpStatusCode.Unauthorized, listed.StatusCode);
    }

    private static IEnumerable<string> MediaFiles(TestInstance instance)
    {
        var media = Path.Combine(instance.RootPath, "media");
        return Directory.Exists(media)
            ? Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories)
            : [];
    }

    private static int CountMediaFiles(TestInstance instance) => MediaFiles(instance).Count();

    private static string DescribeMediaFiles(TestInstance instance) =>
        string.Join(", ", MediaFiles(instance).Select(path => Path.GetRelativePath(instance.RootPath, path)));

    private static async Task<HttpResponseMessage> UploadVoiceAsync(
        HttpClient client,
        string idempotencyKey,
        string createdAtUtc = "2026-03-01T15:50:00.0000000+00:00",
        int createdOffsetMinutes = 480)
    {
        var audio = new ByteArrayContent(FakeAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");

        using var form = new MultipartFormDataContent
        {
            { audio, VoiceUploadFields.Audio, "capture.m4a" },
            { new StringContent(createdAtUtc), VoiceUploadFields.CreatedAtUtc },
            { new StringContent(createdOffsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), VoiceUploadFields.CreatedOffsetMinutes },
            { new StringContent("3520"), VoiceUploadFields.DurationMilliseconds },
            { new StringContent(idempotencyKey), VoiceUploadFields.IdempotencyKey },
        };

        return await client.PostAsync("/api/inputs/voice", form);
    }

    /// <summary>Polls until the entry reaches the expected state, because transcription is asynchronous by design.</summary>
    private static async Task<InputDto> WaitForAsync(
        HttpClient client,
        string inputId,
        Func<InputDto, bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(40));
        InputDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/inputs/{inputId}");
            response.EnsureSuccessStatusCode();

            last = await response.Content.ReadFromJsonAsync<InputDto>();

            if (last is not null && condition(last))
            {
                return last;
            }

            await Task.Delay(250);
        }

        Assert.Fail(
            $"The entry did not reach the expected state in time. Last seen: " +
            $"status={last?.TranscriptionStatus}, job={last?.TranscriptionJobStatus}, attempts={last?.TranscriptionJobAttempts}, failure={last?.FailureCode}");

        throw new InvalidOperationException("unreachable");
    }

    private static Task<InputDto> WaitForTranscriptionAsync(TestInstance instance, HttpClient device, string inputId)
    {
        _ = instance;

        // Waits for the job to be terminal, not just for the transcript to appear. Phase two's post-transcription
        // steps (topic recognition, embedding enqueue) run after the entry is marked transcribed, so the entry
        // reaching "succeeded" no longer implies the job has finished — and asserting the job's final state is
        // what this wait is for.
        return WaitForAsync(
            device,
            inputId,
            view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded &&
                    view.TranscriptionJobStatus is JobStatusNames.Succeeded or JobStatusNames.Failed);
    }
}
