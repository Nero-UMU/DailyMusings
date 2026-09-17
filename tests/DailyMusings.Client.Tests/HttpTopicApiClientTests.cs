using DailyMusings.Client.Core;
using System.Net;
using DailyMusings.Client.Core.Http;
using DailyMusings.Client.Core.Topics;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// The topic vocabulary over HTTP (docs/开发指导.md §6.2, §13). The screens that use it are the ones that keep the
/// vocabulary usable — browse, rename, merge, file by hand — so what is asserted here is that each call goes to the
/// right place with the right body, and that a refusal arrives as a code rather than as an exception.
/// </summary>
[TestClass]
public class HttpTopicApiClientTests
{
    private const string TopicBody = """
        {"id":"topic-1","name":"晨跑","createdAtUtc":"2026-03-01T19:00:00Z","mergedIntoId":null,"mergedAtUtc":null}
        """;

    /// <summary>Reads the single request body the stub recorded back into the contract type it came from.</summary>
    private static T Read<T>(StubHttpHandler handler) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            handler.Bodies.Single(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static HttpTopicApiClient Client(StubHttpHandler handler, string? token = "device-token") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") }, new StubTokenProvider(token));

    [TestMethod]
    public async Task Listing_asks_for_active_topics_only_unless_an_audit_view_wants_the_tombstones()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, """{"items":[""" + TopicBody + """]}""");

        var result = await Client(handler).ListAsync(includeMerged: false, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Value!.Count);
        Assert.AreEqual("晨跑", result.Value[0].Name);

        var request = handler.Requests.Single();
        Assert.AreEqual("/api/topics", request.RequestUri!.AbsolutePath);
        StringAssert.Contains(request.RequestUri.Query, "includeMerged=false");
    }

    [TestMethod]
    public async Task Creating_a_topic_sends_the_name_and_keeps_the_servers_answer()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.Created, TopicBody);

        var result = await Client(handler).CreateAsync("晨跑", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("topic-1", result.Value!.Id);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/api/topics", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("晨跑", Read<CreateTopicRequest>(handler).Name);
    }

    [TestMethod]
    public async Task Renaming_targets_the_topic_and_sends_only_the_new_name()
    {
        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, TopicBody);

        await Client(handler).RenameAsync("topic-1", "晨跑与散步", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/api/topics/topic-1", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("晨跑与散步", Read<RenameTopicRequest>(handler).Name);
    }

    [TestMethod]
    public async Task Merging_sends_both_ends_and_reports_how_many_entries_moved()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.OK,
            """{"source":""" + TopicBody + ""","remappedInputs":3}""");

        var result = await Client(handler).MergeAsync("topic-1", "topic-2", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Value!.RemappedInputs);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/api/topics/merge", request.RequestUri!.AbsolutePath);

        var body = Read<MergeTopicsRequest>(handler);
        Assert.AreEqual("topic-1", body.SourceTopicId);
        Assert.AreEqual("topic-2", body.TargetTopicId);
    }

    [TestMethod]
    public async Task Filing_an_entry_by_hand_uses_the_inputs_own_url()
    {
        var handler = StubHttpHandler.AlwaysJson(
            HttpStatusCode.OK,
            """{"inputId":"input-1","primaryTopicId":"topic-1","secondaryTopicIds":["topic-2"]}""");

        var result = await Client(handler).AssignAsync("input-1", "topic-1", ["topic-2"], CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("topic-1", result.Value!.PrimaryTopicId);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Put, request.Method);
        Assert.AreEqual("/api/inputs/input-1/topics", request.RequestUri!.AbsolutePath);

        var body = Read<AssignInputTopicsRequest>(handler);
        Assert.AreEqual("topic-1", body.PrimaryTopicId);
        CollectionAssert.AreEqual(new[] { "topic-2" }, body.SecondaryTopicIds!.ToArray());
    }

    [TestMethod]
    public async Task A_refusal_comes_back_as_the_servers_own_code()
    {
        var client = Client(StubHttpHandler.AlwaysJson(
            HttpStatusCode.Conflict,
            """{"code":"topic.merge.same_topic","message":"..."}"""));

        var result = await client.MergeAsync("topic-1", "topic-1", CancellationToken.None);

        Assert.IsTrue(result.ServerReached);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("topic.merge.same_topic", result.FailureCode);
    }

    [TestMethod]
    public async Task An_unreachable_server_and_an_unpaired_device_are_told_apart()
    {
        var unreachable = await Client(StubHttpHandler.Throwing(new HttpRequestException("no route")))
            .ListAsync(includeMerged: false, CancellationToken.None);

        Assert.IsFalse(unreachable.ServerReached);
        Assert.AreEqual("client.network_unreachable", unreachable.FailureCode);

        var handler = StubHttpHandler.AlwaysJson(HttpStatusCode.OK, """{"items":[]}""");
        var unpaired = await Client(handler, token: null).ListAsync(includeMerged: false, CancellationToken.None);

        Assert.AreEqual("client.not_paired", unpaired.FailureCode);
        Assert.AreEqual(0, handler.Requests.Count, "Nothing may leave the device before it is paired.");
    }
}
