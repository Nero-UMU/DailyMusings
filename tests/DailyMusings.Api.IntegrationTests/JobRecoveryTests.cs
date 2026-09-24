using System.Net.Http.Headers;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// §14 and §15.3: a process that dies in the middle of a job must not leave that work stranded.
/// <para>
/// The executor requeues everything still marked <c>running</c> the moment it starts, because at that point
/// nothing in this instance can legitimately be running. A stopping executor deliberately leaves the row alone
/// rather than pretending the work failed — only the next startup can tell an interruption from a real failure,
/// which is exactly what this test pins down against a real database and a real restart.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class JobRecoveryTests
{
    private static readonly byte[] FakeAudio = "not-really-m4a-but-the-stub-does-not-care"u8.ToArray();

    private static Dictionary<string, string?> TranscriptionEnabled(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["Transcription:Enabled"] = "true",
        ["Transcription:BaseUrl"] = baseUrl,
        ["Transcription:Model"] = "test-whisper",
        ["Transcription:SecretName"] = "openai-api-key",

        // Generous, so the only thing that can end the stalled call is the shutdown itself.
        ["Transcription:TimeoutSeconds"] = "300",
    };

    [TestMethod]
    public async Task A_job_left_running_by_a_killed_process_is_requeued_on_startup()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-api", Guid.CreateVersion7().ToString("N"));

        // The endpoint takes the call and never answers, so the job is still running when the process stops.
        await using var hanging = await StubTranscriptionEndpoint.StartAsync();
        hanging.State.ResponseDelay = TimeSpan.FromMinutes(5);

        var first = await TestInstance.StartAtAsync(root, TranscriptionEnabled(hanging.BaseUrl));
        TestInstance? second = null;

        try
        {
            first.WriteSecret("openai-api-key", "test-api-key");
            await first.SignInAsChangedAdministratorAsync();
            var (_, deviceClient) = await first.PairDeviceAsync();

            var token = deviceClient.DefaultRequestHeaders.Authorization!.Parameter!;

            using var upload = await UploadVoiceAsync(deviceClient, "interrupted");
            upload.EnsureSuccessStatusCode();
            var ingested = await upload.Content.ReadFromJsonAsync<IngestResponse>();

            var running = await WaitForAsync(
                deviceClient,
                ingested!.Input.Id,
                view => view.TranscriptionJobStatus == JobStatusNames.Running);

            Assert.AreEqual(1, running.TranscriptionJobAttempts, "The job was claimed exactly once so far.");

            // The process goes away mid-job, exactly as a killed container would. Nothing says "recover me" yet:
            // the record is simply left running, which is the state this test is about.
            await first.StopAsync();

            var databasePath = Path.Combine(root, "data", "dailymusings.db");
            Assert.IsTrue(File.Exists(databasePath), $"The instance database must exist at {databasePath}.");

            Assert.AreEqual(
                JobStatusNames.Running,
                await ReadJobStatusAsync(databasePath, ingested.Input.Id),
                "A shutdown mid-job must leave the row running rather than inventing a failure for it.");

            // Second start, same instance directory, with an endpoint that answers.
            await using var working = await StubTranscriptionEndpoint.StartAsync();

            second = await TestInstance.StartAtAsync(root, TranscriptionEnabled(working.BaseUrl));

            Assert.IsFalse(
                second.BootstrapOutput.Contains("INITIAL-ADMIN-PASSWORD", StringComparison.Ordinal),
                "Restarting an existing instance must not generate a second administrator password.");

            // The device token still works, and the interrupted job finishes without anyone asking for it.
            using var device = new HttpClient { BaseAddress = second.Client.BaseAddress };
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var recovered = await WaitForAsync(
                device,
                ingested.Input.Id,
                view => view.TranscriptionStatus == TranscriptionStatusNames.Succeeded,
                timeout: TimeSpan.FromSeconds(60));

            Assert.AreEqual(working.State.ResponseText, recovered.Transcript);

            Assert.AreEqual(
                2,
                recovered.TranscriptionJobAttempts,
                "The recovered work must be the same job record tried again, not a brand-new job.");

            Assert.AreEqual(JobStatusNames.Succeeded, recovered.TranscriptionJobStatus);

            // And the original audio was still there to send: the interruption lost nothing.
            Assert.IsTrue(recovered.HasAudio);
            Assert.AreEqual(FakeAudio.Length, working.State.LastAudioBytes);
        }
        finally
        {
            await first.StopAsync();

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>
    /// Reads the job row straight out of the instance database. The point of the assertion is the state the
    /// process left behind, which no running instance can report afterwards.
    /// </summary>
    private static async Task<string> ReadJobStatusAsync(string databasePath, string inputId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString());

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM processing_job WHERE target_id = $target AND job_type = $type;";
        command.Parameters.AddWithValue("$target", inputId);
        command.Parameters.AddWithValue("$type", (int)DailyMusings.Domain.Jobs.JobType.Transcription);

        var value = await command.ExecuteScalarAsync();

        Assert.IsNotNull(value, $"No transcription job row for {inputId} was left in the database.");

        return (long)value switch
        {
            (long)DailyMusings.Domain.Jobs.JobStatus.Pending => JobStatusNames.Pending,
            (long)DailyMusings.Domain.Jobs.JobStatus.Running => JobStatusNames.Running,
            (long)DailyMusings.Domain.Jobs.JobStatus.Succeeded => JobStatusNames.Succeeded,
            (long)DailyMusings.Domain.Jobs.JobStatus.Failed => JobStatusNames.Failed,
            var other => $"unknown({other})",
        };
    }

    private static async Task<HttpResponseMessage> UploadVoiceAsync(HttpClient client, string idempotencyKey)
    {
        var audio = new ByteArrayContent(FakeAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");

        using var form = new MultipartFormDataContent
        {
            { audio, VoiceUploadFields.Audio, "capture.m4a" },
            { new StringContent("2026-03-01T15:50:00.0000000+00:00"), VoiceUploadFields.CreatedAtUtc },
            { new StringContent("480"), VoiceUploadFields.CreatedOffsetMinutes },
            { new StringContent("3520"), VoiceUploadFields.DurationMilliseconds },
            { new StringContent(idempotencyKey), VoiceUploadFields.IdempotencyKey },

            // The queue does the work in this test, on purpose: what it is about is a process that dies while a
            // *job* is running, so it asks the upload not to hold the transcription itself. With the inline
            // attempt left on, the hanging endpoint would keep the upload request open for the whole inline
            // window before the job was even reachable.
            { new StringContent("false"), VoiceUploadFields.TranscribeNow },
        };

        return await client.PostAsync("/api/inputs/voice", form);
    }

    /// <summary>Polls until the entry reaches the expected state, because the queue is asynchronous by design.</summary>
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
}
