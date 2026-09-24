using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The whole phase-three loop over real HTTP: capture, topic filing, generation against a controllable model
/// endpoint, source mapping, the second-stage check and confirmation
/// (docs/开发指导.md §7, §8.3, §8.4, §13, §17.3 steps 3–5).
/// <para>
/// The model endpoint is a stand-in, because the product bundles no model; everything else — the queue, the
/// scheduler, the database, the retrieval path, the API surface — is the real thing.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class ReflectionLoopTests
{
    private static Dictionary<string, string?> GenerationEnabled(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["Generation:Enabled"] = "true",
        ["Generation:BaseUrl"] = baseUrl,
        ["Generation:Model"] = "test-writer",
        ["Generation:PromptVersion"] = "generation-test-v1",
        ["Generation:SecretName"] = "openai-api-key",
        ["Generation:TimeoutSeconds"] = "30",

        // The scan runs every couple of seconds in tests so the nightly path is exercised without waiting a day.
        ["Scheduler:IntervalSeconds"] = "1",
    };

    [TestMethod]
    public async Task A_day_is_written_checked_and_confirmed()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        var topic = await CreateTopicAsync(instance.Client, "记录");

        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天试着记录了一点东西。", DateTimeOffset.UtcNow.ToString("o"), 480, "capture-1"));

        captured.EnsureSuccessStatusCode();
        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.IsNotNull(ingested);

        // §8.2 step 5: the text is recognized against the topics the user keeps, as it arrives.
        var filed = await WaitForTopicsAsync(instance.Client, ingested.Input.Id);
        Assert.AreEqual(topic.Id, filed.PrimaryTopicId);
        Assert.AreEqual(0, filed.SecondaryTopicIds.Count, "§6.2: the primary is never also a secondary.");

        var today = ingested.Input.ContentDate;

        using var requested = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/generate",
            new GenerateReflectionRequest(IgnoreTranscriptionFailures: false, AllowOverwriteOfManualEdits: false));

        requested.EnsureSuccessStatusCode();
        var queued = await requested.Content.ReadFromJsonAsync<ReflectionGenerationResponse>();
        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.Queued, queued.Code);
        Assert.IsNotNull(queued.Job);

        var draft = await WaitForDraftAsync(instance.Client, today, ReflectionStatusNames.ReviewRequired);

        Assert.IsNotNull(draft.WorkingVersion);
        Assert.AreEqual("今天的记录", draft.WorkingVersion.Title);
        Assert.AreEqual("test-writer", draft.WorkingVersion.ModelName);
        Assert.AreEqual("generation-test-v1", draft.WorkingVersion.PromptVersion);

        // The model was asked with the system rules that may not be overridden, and with the day's material.
        Assert.AreEqual("Bearer test-api-key", model.State.LastAuthorization);
        Assert.AreEqual("test-writer", model.State.LastModel);
        Assert.IsTrue(
            model.State.LastSystemPrompt!.Contains("不得添加任何素材中不存在的事实", StringComparison.Ordinal),
            "§8.4's unoverridable system rules must travel with every request.");
        Assert.IsTrue(model.State.LastUserPrompt!.Contains("今天试着记录了一点东西。", StringComparison.Ordinal));

        // The source map is derived from the draft the model produced, not from offsets it reported (A.6).
        Assert.AreEqual(1, draft.WorkingVersion.Sources.Count);
        var source = draft.WorkingVersion.Sources[0];
        Assert.AreEqual(ingested.Input.Id, source.InputId);
        Assert.AreEqual(SourceDriftNames.Exact, source.Drift);
        Assert.IsFalse(source.IsHistorical);
        Assert.AreEqual(
            "今天试着记录了一点东西。",
            Paragraph(draft.WorkingVersion.Body, source.BlockIndex)[source.CharStart..source.CharEnd]);

        // §8.4's second stage ran, and its finding is reported as a warning rather than a block.
        var sources = await WaitForCheckAsync(instance.Client, today);
        Assert.IsNotNull(sources.CheckedAtUtc, "A completed check is stamped; an empty finding list alone is not enough.");
        Assert.AreEqual(1, sources.UnsourcedClaims.Count);
        Assert.AreEqual(
            2,
            sources.UnsourcedClaims[0].BlockIndex,
            "The flagged sentence is the third paragraph of the draft the model returned.");
        Assert.AreEqual("素材里没有提到这件事", sources.UnsourcedClaims[0].Reason);

        using var unsourced = await instance.Client.GetAsync($"/api/reflections/{today}/sources");
        var report = await unsourced.Content.ReadFromJsonAsync<ReflectionSourcesResponse>();
        Assert.AreEqual(
            "我记得那天的风很大。",
            Paragraph(draft.WorkingVersion.Body, report!.UnsourcedClaims[0].BlockIndex)[
                report.UnsourcedClaims[0].CharStart..report.UnsourcedClaims[0].CharEnd]);

        // Confirming while findings are unacknowledged is refused rather than silently accepted.
        using var unacknowledged = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/confirm",
            new ConfirmReflectionRequest(AcceptedUnsourcedClaims: false));

        Assert.AreEqual(HttpStatusCode.Conflict, unacknowledged.StatusCode);

        using var confirmed = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/confirm",
            new ConfirmReflectionRequest(AcceptedUnsourcedClaims: true));

        confirmed.EnsureSuccessStatusCode();
        var confirmedDraft = await confirmed.Content.ReadFromJsonAsync<ReflectionDto>();
        Assert.AreEqual(ReflectionStatusNames.Confirmed, confirmedDraft!.Status);
        Assert.AreEqual(confirmedDraft.WorkingVersionId, confirmedDraft.ConfirmedVersionId);

        // §3.1 keeps embeddings optional, so an instance without them reports degraded retrieval rather than an
        // error — and never lets an external service affect the basic health check (§16, A.8).
        var semantic = await instance.Client.GetFromJsonAsync<SemanticSearchDto>("/api/system/semantic-search");
        Assert.IsNotNull(semantic);
        Assert.IsFalse(semantic.Enabled);
        Assert.IsFalse(semantic.Available);
        Assert.IsFalse(semantic.Rebuilding);
    }

    /// <summary>
    /// §8.1/§14: a rate-limited writing endpoint is temporary trouble. The day must be visibly failed (so the
    /// user is not left watching "generating" forever) while the job keeps its place in the queue, and the
    /// automatic retry must produce the draft once the endpoint behaves — with no user action.
    /// </summary>
    [TestMethod]
    public async Task A_rate_limited_writing_endpoint_is_retried_and_the_draft_still_appears()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        model.State.FailWithStatusCode = HttpStatusCode.TooManyRequests;

        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));
        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天试着记录了一点东西。", DateTimeOffset.UtcNow.ToString("o"), 480, "rate-limited"));
        captured.EnsureSuccessStatusCode();

        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();
        var today = ingested!.Input.ContentDate;

        using var requested = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/generate",
            new GenerateReflectionRequest(IgnoreTranscriptionFailures: false, AllowOverwriteOfManualEdits: false));
        requested.EnsureSuccessStatusCode();

        var failed = await WaitForDraftAsync(instance.Client, today, ReflectionStatusNames.Failed);

        // The day says it failed, and the failure is the endpoint's rate limit rather than an internal defect.
        Assert.IsNull(failed.WorkingVersion, "Nothing may be written while the endpoint is refusing.");

        var job = await WaitForJobAsync(device, today, "ReflectionGeneration", JobStatusNames.Pending);

        Assert.AreEqual("generation.upstream_unavailable", job.ErrorCode);
        Assert.AreEqual(1, job.AttemptCount);

        // Once the endpoint stops rate-limiting, the queued retry writes the draft by itself. §14's backoff is a
        // minute, so the window has to be comfortably longer than that.
        model.State.Reset();

        var draft = await WaitForDraftAsync(
            instance.Client,
            today,
            ReflectionStatusNames.ReviewRequired,
            timeout: TimeSpan.FromSeconds(150));

        Assert.IsNotNull(draft.WorkingVersion);
        Assert.AreEqual("今天的记录", draft.WorkingVersion.Title);
        Assert.IsTrue(model.State.RequestCount >= 2, "The retry really did call the endpoint again.");
    }

    [TestMethod]
    public async Task An_arbitrary_past_day_cannot_be_generated()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();

        var pastDay = DateTimeOffset.UtcNow.AddDays(-5).ToString("yyyy-MM-dd");

        using var response = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{pastDay}/generate",
            new GenerateReflectionRequest(false, false));

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReflectionGenerationResponse>();
        Assert.IsNotNull(body);
        Assert.IsFalse(body.Queued);
        Assert.AreEqual("reflection.regeneration.date_not_current", body.Code);

        // Regeneration is only offered for a day that is actually waiting for it.
        using var stale = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{pastDay}/regenerate-stale",
            new GenerateReflectionRequest(false, false));

        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [TestMethod]
    public async Task A_day_with_no_input_has_no_draft()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();

        // The instance's content day, not UTC's: see TestInstance.ContentDateAsync.
        var today = await instance.ContentDateAsync();

        using var generated = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/generate",
            new GenerateReflectionRequest(false, false));

        Assert.AreEqual(HttpStatusCode.Conflict, generated.StatusCode);

        var body = await generated.Content.ReadFromJsonAsync<ReflectionGenerationResponse>();
        Assert.AreEqual("reflection.generation.no_inputs", body!.Code);

        // §7: 当天无输入时不得创建空文章 — and the read side agrees there is nothing there.
        using var read = await instance.Client.GetAsync($"/api/reflections/{today}");
        Assert.AreEqual(HttpStatusCode.NotFound, read.StatusCode);

        Assert.AreEqual(0, model.State.RequestCount, "Nothing may be sent to a model for a day with no material.");
    }

    [TestMethod]
    public async Task Topics_can_be_created_renamed_merged_and_used_to_file_an_input()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        var source = await CreateTopicAsync(instance.Client, "录音上传");
        var target = await CreateTopicAsync(instance.Client, "录音");

        // Creating the same topic again is not an error and does not produce a second row.
        using var again = await instance.Client.PostAsJsonAsync("/api/topics", new CreateTopicRequest("录音"));
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        Assert.AreEqual(target.Id, (await again.Content.ReadFromJsonAsync<TopicDto>())!.Id);

        var listed = await instance.Client.GetFromJsonAsync<TopicListResponse>("/api/topics");
        Assert.AreEqual(2, listed!.Items.Count);

        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天录了一段音。", DateTimeOffset.UtcNow.ToString("o"), 480, "topic-capture"));

        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();

        using var assigned = await instance.Client.PutAsJsonAsync(
            $"/api/inputs/{ingested!.Input.Id}/topics",
            new AssignInputTopicsRequest(source.Id, [target.Id]));

        assigned.EnsureSuccessStatusCode();
        var assignment = await assigned.Content.ReadFromJsonAsync<InputTopicAssignmentResponse>();
        Assert.AreEqual(source.Id, assignment!.PrimaryTopicId);
        CollectionAssert.AreEqual(new[] { target.Id }, assignment.SecondaryTopicIds.ToArray());

        using var renamed = await instance.Client.PatchAsJsonAsync(
            $"/api/topics/{source.Id}",
            new RenameTopicRequest("录音与上传"));

        renamed.EnsureSuccessStatusCode();
        Assert.AreEqual("录音与上传", (await renamed.Content.ReadFromJsonAsync<TopicDto>())!.Name);

        using var merged = await instance.Client.PostAsJsonAsync(
            "/api/topics/merge",
            new MergeTopicsRequest(source.Id, target.Id));

        merged.EnsureSuccessStatusCode();
        var mergeResult = await merged.Content.ReadFromJsonAsync<TopicMergeResponse>();
        Assert.AreEqual(1, mergeResult!.RemappedInputs, "The filed input changes hands.");
        Assert.IsTrue(mergeResult.Source.MergedIntoId is not null, "A merge leaves a tombstone, never a delete (A.9).");

        // The merged topic is gone from the default list but still resolvable, so nothing that referenced it dangles.
        var afterMerge = await instance.Client.GetFromJsonAsync<TopicListResponse>("/api/topics");
        Assert.AreEqual(1, afterMerge!.Items.Count);

        var withMerged = await instance.Client.GetFromJsonAsync<TopicListResponse>("/api/topics?includeMerged=true");
        Assert.AreEqual(2, withMerged!.Items.Count);

        // Filing new material under a retired topic is refused instead of quietly resurrecting it.
        using var retired = await instance.Client.PutAsJsonAsync(
            $"/api/inputs/{ingested.Input.Id}/topics",
            new AssignInputTopicsRequest(source.Id, null));

        Assert.AreEqual(HttpStatusCode.Conflict, retired.StatusCode);
    }

    [TestMethod]
    public async Task An_unauthenticated_caller_cannot_read_or_write_reflections()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        using var anonymous = new HttpClient { BaseAddress = instance.Client.BaseAddress };
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        using var read = await anonymous.GetAsync($"/api/reflections/{today}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, read.StatusCode);

        using var generate = await anonymous.PostAsJsonAsync(
            $"/api/reflections/{today}/generate",
            new GenerateReflectionRequest(false, false));

        Assert.AreEqual(HttpStatusCode.Unauthorized, generate.StatusCode);

        using var topics = await anonymous.GetAsync("/api/topics");
        Assert.AreEqual(HttpStatusCode.Unauthorized, topics.StatusCode);
    }

    private static async Task<TopicDto> CreateTopicAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/topics", new CreateTopicRequest(name));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TopicDto>())!;
    }

    /// <summary>
    /// Waits for the entry to be filed. Topic recognition is not a job of its own for a typed note, but it is
    /// still asynchronous from the client's point of view — it happens after the capture is stored.
    /// </summary>
    private static async Task<InputDto> WaitForTopicsAsync(HttpClient client, string inputId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40);
        InputDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await client.GetFromJsonAsync<InputDto>($"/api/inputs/{inputId}");

            if (last?.PrimaryTopicId is not null)
            {
                return last;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The input was not filed under a topic in time. Last seen: {last?.TranscriptionStatus}");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<ReflectionDto> WaitForDraftAsync(
        HttpClient client,
        string contentDate,
        string status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        ReflectionDto? last = null;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/reflections/{contentDate}");

            if (response.IsSuccessStatusCode)
            {
                last = await response.Content.ReadFromJsonAsync<ReflectionDto>();
                if (last?.Status == status)
                {
                    return last;
                }
            }
            else
            {
                lastError = $"{(int)response.StatusCode}";
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The draft did not reach {status} in time. Last seen: {last?.Status ?? lastError}");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<ReflectionSourcesResponse> WaitForCheckAsync(HttpClient client, string contentDate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        ReflectionSourcesResponse? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/reflections/{contentDate}/sources");

            if (response.IsSuccessStatusCode)
            {
                last = await response.Content.ReadFromJsonAsync<ReflectionSourcesResponse>();
                if (last?.CheckedAtUtc is not null)
                {
                    return last;
                }
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The source check did not complete in time. Checked at: {last?.CheckedAtUtc ?? "never"}");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Polls the queue for one target's job. The retry classification — queued again versus given up on — is only
    /// visible here, which is why the API exposes the job beside the day it belongs to.
    /// </summary>
    private static async Task<JobDto> WaitForJobAsync(HttpClient client, string targetId, string jobType, string status)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        JobDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var jobs = await client.GetFromJsonAsync<JobListResponse>("/api/jobs?limit=100");
            last = jobs?.Items.FirstOrDefault(item => item.JobType == jobType && item.TargetId == targetId);

            if (last is { ErrorCode: not null } && last.Status == status)
            {
                return last;
            }

            await Task.Delay(250);
        }

        Assert.Fail(
            $"No {jobType} job for {targetId} reported {status} with an error code in time. " +
            $"Last seen: {last?.Status ?? "none"} / {last?.ErrorCode ?? "no code"}");

        throw new InvalidOperationException("unreachable");
    }

    private static string Paragraph(string body, int blockIndex) =>
        body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n")[blockIndex].Trim('\n');
}
