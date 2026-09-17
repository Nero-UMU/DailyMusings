using System.Net;
using System.Text;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The draft screen's HTTP half (docs/开发指导.md §6.3, §8.4, §11.1). What matters here is that the server's own
/// refusal codes survive the trip: several of them are instructions to the user rather than errors, and a client
/// that flattened them into "request failed" would leave the user with nothing to do.
/// </summary>
[TestClass]
public class HttpReflectionApiClientTests
{
    private const string ReflectionBody = """
        {"id":"reflection-1","contentDate":"2026-03-01","status":"reviewRequired","generationReason":"manual",
        "lastStaleReason":null,"initialVersionId":"version-1","previousVersionId":null,"workingVersionId":"version-1",
        "confirmedVersionId":null,"confirmedVersionIsNotWorking":false,"createdAtUtc":"2026-03-01T19:00:00Z",
        "updatedAtUtc":"2026-03-01T19:00:00Z","initialVersion":null,"previousVersion":null,
        "workingVersion":{"id":"version-1","title":"标题","summary":"摘要","body":"正文。","tags":["随想"],
        "categories":["日记"],"hasManualEdits":false,"createdAtUtc":"2026-03-01T19:00:00Z","editedAtUtc":null,
        "modelName":"stub","promptVersion":"v1","sourcesCheckedAtUtc":"2026-03-01T19:00:05Z","sources":[],
        "unsourcedClaims":[]},"semanticSearch":{"enabled":true,"available":true,"rebuilding":false}}
        """;

    /// <summary>Reads the single request body the stub recorded back into the contract type it came from.</summary>
    private static T Read<T>(StubHttpHandler handler) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            handler.Bodies.Single(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static HttpReflectionApiClient Client(
        StubHttpHandler handler,
        string? token = "device-token")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        return new HttpReflectionApiClient(http, new StubTokenProvider(token));
    }

    [TestMethod]
    public async Task Reading_a_day_uses_the_device_token_and_returns_the_draft()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, ReflectionBody);
        var client = Client(handler);

        var result = await client.GetAsync("2026-03-01", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("reviewRequired", result.Value!.Status);
        Assert.AreEqual("正文。", result.Value.WorkingVersion!.Body);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("/api/reflections/2026-03-01", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("device-token", request.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task An_unreachable_server_is_reported_rather_than_thrown()
    {
        var client = Client(StubHttpHandler.Throwing(new HttpRequestException("connection refused")));

        var result = await client.GetAsync("2026-03-01", CancellationToken.None);

        Assert.IsFalse(result.ServerReached);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("client.network_unreachable", result.FailureCode);
    }

    [TestMethod]
    public async Task An_unpaired_client_never_calls_the_server()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, ReflectionBody);
        var client = Client(handler, token: null);

        var result = await client.GetAsync("2026-03-01", CancellationToken.None);

        Assert.AreEqual("client.not_paired", result.FailureCode);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task A_refused_confirmation_keeps_the_servers_own_code()
    {
        const string error = """{"code":"reflection.confirm.unsourced_claims_not_acknowledged","message":"..."}""";

        var client = Client(StubHttpHandler.AlwaysJson(HttpStatusCode.Conflict, error));

        var result = await client.ConfirmAsync("2026-03-01", acceptedUnsourcedClaims: false, CancellationToken.None);

        Assert.IsTrue(result.ServerReached, "The server answered, so this is not a transport failure.");
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("reflection.confirm.unsourced_claims_not_acknowledged", result.FailureCode);
    }

    [TestMethod]
    public async Task Confirming_sends_the_acknowledgement_the_server_requires()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, ReflectionBody);
        var client = Client(handler);

        await client.ConfirmAsync("2026-03-01", acceptedUnsourcedClaims: true, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/api/reflections/2026-03-01/confirm", request.RequestUri!.AbsolutePath);
        Assert.IsTrue(Read<ConfirmReflectionRequest>(handler).AcceptedUnsourcedClaims);
    }

    [TestMethod]
    public async Task Saving_an_edit_sends_the_whole_version_to_the_working_slot()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, ReflectionBody);
        var client = Client(handler);

        await client.EditAsync("2026-03-01", "新标题", "新摘要", "第一段。\n\n第二段。", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/api/reflections/2026-03-01/working-version/content", request.RequestUri!.AbsolutePath);

        // Parsed rather than substring-matched: JsonContent escapes non-ASCII as \uXXXX, and asserting on the wire
        // spelling of a Chinese title would test the encoder instead of the request.
        var body = Read<EditReflectionRequest>(handler);
        Assert.AreEqual("新标题", body.Title);
        Assert.AreEqual("新摘要", body.Summary);
        StringAssert.Contains(body.Body, "第一段。");
    }

    [TestMethod]
    public async Task Asking_for_a_regeneration_carries_both_of_the_users_answers()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, """{"contentDate":"2026-03-01","queued":true,"code":null,"detail":null,"job":null}""");
        var client = Client(handler);

        var result = await client.GenerateAsync(
            "2026-03-01",
            ignoreTranscriptionFailures: true,
            allowOverwriteOfManualEdits: true,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Value!.Queued);

        var body = Read<GenerateReflectionRequest>(handler);
        Assert.IsTrue(body.IgnoreTranscriptionFailures);
        Assert.IsTrue(body.AllowOverwriteOfManualEdits);
    }

    [TestMethod]
    public async Task Publishing_sends_the_target_the_visibility_and_the_replace_flag()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.OK,
            """{"queued":true,"code":null,"detail":null,"publication":null}""");
        var client = Client(handler);

        var result = await client.PublishAsync("2026-03-01", "target-1", PublicationVisibilityNames.Public, replaceExistingFile: true, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);

        var request = handler.Requests.Single();
        Assert.AreEqual("/api/reflections/2026-03-01/publish/target-1", request.RequestUri!.AbsolutePath);

        var body = Read<PublishRequest>(handler);
        Assert.AreEqual(PublicationVisibilityNames.Public, body.Visibility);
        Assert.IsTrue(body.ReplaceExistingFile);
    }

    [TestMethod]
    public async Task A_list_response_is_unwrapped_into_its_items()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.OK,
            """
            {"items":[{"id":"target-1","name":"测试博客","type":"wordPress","destinationReference":null,
            "automaticPublishEnabled":false,"automaticPublishEnabledBy":null,"automaticPublishEnabledAtUtc":null}]}
            """);

        var result = await Client(handler).ListTargetsAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Value!.Count);
        Assert.AreEqual("测试博客", result.Value[0].Name);
        Assert.AreEqual("/api/publish-targets", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task An_answer_that_cannot_be_read_is_reported_rather_than_thrown()
    {
        var client = Client(StubHttpHandler.AlwaysJson(HttpStatusCode.OK, "this is not json"));

        var result = await client.GetAsync("2026-03-01", CancellationToken.None);

        Assert.IsTrue(result.ServerReached);
        Assert.AreEqual("client.malformed_response", result.FailureCode);
    }

    [TestMethod]
    public async Task A_rejected_device_token_is_not_reported_as_an_unreachable_server()
    {
        var client = Client(StubHttpHandler.AlwaysStatus(HttpStatusCode.Unauthorized));

        var result = await client.GetAsync("2026-03-01", CancellationToken.None);

        Assert.IsTrue(result.ServerReached);
        Assert.AreEqual("auth.device_token_rejected", result.FailureCode);
    }
}
