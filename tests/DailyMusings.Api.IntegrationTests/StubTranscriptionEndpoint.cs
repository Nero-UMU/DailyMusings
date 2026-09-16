using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// A stand-in for the user's OpenAI-compatible transcription endpoint (docs/开发指导.md §8.1).
/// <para>
/// The product bundles no model, so the only honest way to test the transcription pipeline is against a
/// controllable endpoint. This one records what it received, so the test can assert the audio actually arrived,
/// the configured model was requested and the secret was presented — none of which a mock of our own interface
/// could prove.
/// </para>
/// </summary>
internal sealed class StubTranscriptionEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubTranscriptionEndpoint(WebApplication app, string baseUrl, StubState state)
    {
        _app = app;
        BaseUrl = baseUrl;
        State = state;
    }

    /// <summary>Value for <c>Transcription:BaseUrl</c>. Ends in <c>/v1</c>, like the real thing.</summary>
    public string BaseUrl { get; }

    public StubState State { get; }

    public static async Task<StubTranscriptionEndpoint> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = Path.GetTempPath(),
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var state = new StubState();

        app.MapPost("/v1/audio/transcriptions", async (HttpContext context) =>
        {
            state.RequestCount++;
            state.LastAuthorization = context.Request.Headers.Authorization.ToString();

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            state.LastModel = form["model"].ToString();
            state.LastLanguage = form["language"].ToString();

            var file = form.Files["file"];
            state.LastFileName = file?.FileName;
            state.LastContentType = file?.ContentType;

            if (file is not null)
            {
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, context.RequestAborted);
                state.LastAudioBytes = buffer.Length;
            }

            if (state.FailWithStatusCode is { } statusCode)
            {
                context.Response.StatusCode = (int)statusCode;
                return;
            }

            // A provider that answers with an unexpected shape, for exercising the client's classification.
            if (state.ResponseBodyOverride is { } overrideBody)
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(overrideBody, context.RequestAborted);
                return;
            }

            await context.Response.WriteAsJsonAsync(
                new { text = state.ResponseText, language = state.ResponseLanguage },
                context.RequestAborted);
        }).DisableAntiforgery();

        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        return new StubTranscriptionEndpoint(app, $"{address.TrimEnd('/')}/v1", state);
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

    internal sealed class StubState
    {
        public int RequestCount { get; set; }

        public string? LastAuthorization { get; set; }

        public string? LastModel { get; set; }

        public string? LastLanguage { get; set; }

        public string? LastFileName { get; set; }

        public string? LastContentType { get; set; }

        public long LastAudioBytes { get; set; }

        public string ResponseText { get; set; } = "今天走了一条没走过的巷子。";

        public string ResponseLanguage { get; set; } = "zh";

        /// <summary>Set to make the stub fail, for exercising the retry classification.</summary>
        public HttpStatusCode? FailWithStatusCode { get; set; }

        /// <summary>Set to answer with a body of a shape the client cannot read.</summary>
        public string? ResponseBodyOverride { get; set; }

        public void Reset()
        {
            RequestCount = 0;
            LastAuthorization = null;
            LastModel = null;
            LastLanguage = null;
            LastFileName = null;
            LastContentType = null;
            LastAudioBytes = 0;
            FailWithStatusCode = null;
            ResponseBodyOverride = null;
        }
    }
}
