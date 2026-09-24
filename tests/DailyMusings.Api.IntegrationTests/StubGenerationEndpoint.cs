using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// A stand-in for the user's OpenAI-compatible chat endpoint (docs/开发指导.md §8.1, §8.4).
/// <para>
/// It answers with the real envelope shape — <c>choices[0].message.content</c> carrying a JSON object — because
/// the pipeline under test parses that content, resolves the citations the model reports against the body it
/// wrote, and stores a quote hash for each. A stub that returned our own DTO directly would skip exactly the part
/// worth testing.
/// </para>
/// </summary>
internal sealed class StubGenerationEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubGenerationEndpoint(WebApplication app, string baseUrl, StubGenerationState state)
    {
        _app = app;
        BaseUrl = baseUrl;
        State = state;
    }

    /// <summary>Value for <c>Generation:BaseUrl</c>. Ends in <c>/v1</c>, like the real thing.</summary>
    public string BaseUrl { get; }

    public StubGenerationState State { get; }

    public static async Task<StubGenerationEndpoint> StartAsync(StubGenerationState? state = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = Path.GetTempPath(),
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        state ??= new StubGenerationState();

        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            state.RequestCount++;
            state.LastAuthorization = context.Request.Headers.Authorization.ToString();

            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted);

            state.LastModel = document.RootElement.TryGetProperty("model", out var model) ? model.GetString() : null;

            var systemPrompt = string.Empty;
            var userPrompt = string.Empty;

            if (document.RootElement.TryGetProperty("messages", out var messages))
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var role = message.GetProperty("role").GetString();
                    var content = message.GetProperty("content").GetString() ?? string.Empty;

                    if (role == "system")
                    {
                        systemPrompt = content;
                    }
                    else
                    {
                        userPrompt = content;
                    }
                }
            }

            state.LastSystemPrompt = systemPrompt;
            state.LastUserPrompt = userPrompt;

            if (state.ResponseDelay is { } delay)
            {
                // A slow endpoint, so the client's own deadline is what ends the call.
                try
                {
                    await Task.Delay(delay, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // The caller gave up (its deadline elapsed, or the process is shutting down).
                    return;
                }
            }

            if (state.FailWithStatusCode is { } statusCode)
            {
                context.Response.StatusCode = (int)statusCode;
                return;
            }

            // The second stage is told apart by the prompt it receives, exactly as a real endpoint would have to.
            var isCheck = userPrompt.Contains("无法从这些素材得到支持", StringComparison.Ordinal);

            var answer = isCheck ? state.CheckContent() : state.DraftContent();

            await context.Response.WriteAsJsonAsync(
                new
                {
                    id = "stub-1",
                    @object = "chat.completion",
                    model = state.LastModel,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = answer },
                            finish_reason = "stop",
                        },
                    },
                },
                context.RequestAborted);
        }).DisableAntiforgery();

        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        return new StubGenerationEndpoint(app, $"{address.TrimEnd('/')}/v1", state);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }
}

internal sealed class StubGenerationState
{
    public int RequestCount { get; set; }

    public string? LastAuthorization { get; set; }

    public string? LastModel { get; set; }

    public string? LastSystemPrompt { get; set; }

    public string? LastUserPrompt { get; set; }

    /// <summary>The sentence the draft is built around, and the one the citations point at.</summary>
    public string GroundedSentence { get; set; } = "今天试着记录了一点东西。";

    /// <summary>
    /// A sentence with no basis in the material, which the source check is expected to flag. Set to empty for a
    /// draft the checker finds clean.
    /// </summary>
    public string UnsourcedSentence { get; set; } = "我记得那天的风很大。";

    /// <summary>Set to make the endpoint answer with an error status, for exercising the retry classification.</summary>
    public HttpStatusCode? FailWithStatusCode { get; set; }

    /// <summary>Set to make the endpoint slow to answer, for exercising the client's own deadline.</summary>
    public TimeSpan? ResponseDelay { get; set; }

    /// <summary>Back to a well-behaved endpoint that answers immediately, keeping the recorded requests.</summary>
    public void Reset()
    {
        FailWithStatusCode = null;
        ResponseDelay = null;
    }

    /// <summary>
    /// Topic names the "model" says the day is about (§6.2 as revised). Empty by default, which is a valid
    /// answer and keeps the tests that are about the text from depending on the filing.
    /// </summary>
    public IReadOnlyList<string> Topics { get; set; } = [];

    /// <summary>New topic names the "model" proposes when the existing vocabulary has nothing that fits.</summary>
    public IReadOnlyList<string> NewTopics { get; set; } = [];

    public string DraftContent()
    {
        var body = $"今天试了一下记录。\n\n{GroundedSentence}";

        if (!string.IsNullOrWhiteSpace(UnsourcedSentence))
        {
            body += $"\n\n{UnsourcedSentence}";
        }

        var citations = new List<object>
        {
            new { quote = GroundedSentence, sources = new[] { "S1" }, relevance = 0.9, reason = "来自今天的记录" },
        };

        return JsonSerializer.Serialize(new
        {
            title = "今天的记录",
            summary = "把今天的话留下来。",
            body,
            tags = new[] { "记录" },
            categories = new[] { "随想" },
            topics = Topics,
            newTopics = NewTopics,
            citations,
        });
    }

    public string CheckContent() =>
        string.IsNullOrWhiteSpace(UnsourcedSentence)
            ? JsonSerializer.Serialize(new { unsourced = Array.Empty<object>() })
            : JsonSerializer.Serialize(new
            {
                unsourced = new[]
                {
                    new { quote = UnsourcedSentence, reason = "素材里没有提到这件事" },
                },
            });
}
