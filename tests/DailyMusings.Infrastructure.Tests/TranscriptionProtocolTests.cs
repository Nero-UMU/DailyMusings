using System.Net;
using System.Text;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

[TestClass]
public sealed class TranscriptionProtocolTests
{
    [TestMethod]
    public async Task OpenAI_transcription_uses_multipart_and_joins_a_trailing_slash_once()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"text":"识别成功"}"""));
        var client = Client(handler, TranscriptionApiTypes.OpenAiTranscription, "qwen-looking-model", "https://api.example.com/v1/");

        var result = await client.TranscribeAsync(LocalRecording(), CancellationToken.None);

        Assert.AreEqual("识别成功", result.Text);
        var sent = handler.Requests.Single();
        Assert.AreEqual("https://api.example.com/v1/audio/transcriptions", sent.Url);
        Assert.AreEqual("Bearer test-key", sent.Authorization);
        StringAssert.Contains(sent.ContentType, "multipart/form-data");
        StringAssert.Contains(sent.Body, "name=file");
        StringAssert.Contains(sent.Body, "name=model");
        StringAssert.Contains(sent.Body, "qwen-looking-model");
    }

    [TestMethod]
    public async Task OpenAI_chat_audio_is_independent_and_uses_the_exact_model()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"choices":[{"message":{"content":"聊天协议识别成功"}}]}"""));
        var client = Client(handler, TranscriptionApiTypes.OpenAiChatAudio, "arbitrary-model-name");

        var result = await client.TranscribeAsync(LocalRecording(), CancellationToken.None);

        Assert.AreEqual("聊天协议识别成功", result.Text);
        var sent = handler.Requests.Single();
        Assert.AreEqual("https://api.example.com/v1/chat/completions", sent.Url);
        StringAssert.Contains(sent.ContentType, "application/json");
        using var payload = JsonDocument.Parse(sent.Body);
        var root = payload.RootElement;
        Assert.AreEqual("arbitrary-model-name", root.GetProperty("model").GetString());
        Assert.IsFalse(root.GetProperty("stream").GetBoolean());
        Assert.IsFalse(root.GetProperty("asr_options").GetProperty("enable_itn").GetBoolean());

        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.AreEqual(1, content.GetArrayLength());
        Assert.AreEqual("input_audio", content[0].GetProperty("type").GetString());
        Assert.AreEqual(
            "data:audio/mp4;base64,YXVkaW8=",
            content[0].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.IsFalse(content[0].GetProperty("input_audio").TryGetProperty("format", out _));
    }

    [TestMethod]
    public async Task DashScope_async_refuses_a_local_file_instead_of_faking_a_url()
    {
        var handler = new RecordingHandler((_, _) => throw new AssertFailedException("No request should be sent."));
        var client = Client(handler, TranscriptionApiTypes.DashScopeAsync, "any-model");

        var failure = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(() =>
            client.TranscribeAsync(LocalRecording(), CancellationToken.None));

        Assert.AreEqual("transcription.public_audio_url_required", failure.Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DashScope_async_hides_task_submission_polling_and_result_download()
    {
        var handler = new RecordingHandler((request, call) => call switch
        {
            1 => Json("""{"output":{"task_id":"task-42","task_status":"PENDING"}}"""),
            2 => Json("""{"output":{"task_id":"task-42","task_status":"SUCCEEDED","results":[{"transcription_url":"https://files.example/result.json"}]}}"""),
            3 => Json("""{"transcripts":[{"text":"异步识别成功"}]}"""),
            _ => throw new AssertFailedException($"Unexpected request: {request.RequestUri}"),
        });
        var client = Client(handler, TranscriptionApiTypes.DashScopeAsync, "user-dash-model", "https://api.example.com/api/v1/");
        var request = LocalRecording() with { PublicAudioUrl = new Uri("https://media.example/audio.m4a") };

        var result = await client.TranscribeAsync(request, CancellationToken.None);

        Assert.AreEqual("异步识别成功", result.Text);
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual("https://api.example.com/api/v1/services/audio/asr/transcription", handler.Requests[0].Url);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        Assert.AreEqual("enable", handler.Requests[0].DashScopeAsyncHeader);
        StringAssert.Contains(handler.Requests[0].Body, "\"model\":\"user-dash-model\"");
        StringAssert.Contains(handler.Requests[0].Body, "https://media.example/audio.m4a");
        Assert.AreEqual("https://api.example.com/api/v1/tasks/task-42", handler.Requests[1].Url);
        Assert.AreEqual("GET", handler.Requests[1].Method);
        Assert.AreEqual("https://files.example/result.json", handler.Requests[2].Url);
    }

    private static TranscriptionRequest LocalRecording() => new(
        _ => Task.FromResult<Stream>(new MemoryStream("audio"u8.ToArray())),
        "capture.m4a",
        "audio/mp4");

    private static OpenAiCompatibleTranscriptionClient Client(
        HttpMessageHandler handler,
        string apiType,
        string model,
        string baseUrl = "https://api.example.com/v1") => new(
            new HttpClient(handler),
            new SecretStore(),
            new SettingsProvider(TranscriptionSettings.Default with
            {
                Enabled = true,
                BaseUrl = baseUrl,
                Model = model,
                SecretName = "transcription-key",
                ApiType = apiType,
            }),
            NullLogger<OpenAiCompatibleTranscriptionClient>.Instance);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("X-DashScope-Async", out var values) ? values.Single() : null,
                body);
            Requests.Add(recorded);
            return respond(request, Requests.Count);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Url,
        string? Authorization,
        string ContentType,
        string? DashScopeAsyncHeader,
        string Body);

    private sealed class SecretStore : ISecretStore
    {
        public string? TryGet(string name) => name == "transcription-key" ? "test-key" : null;
        public bool Exists(string name) => TryGet(name) is not null;
        public IReadOnlyList<string> ListNames() => ["transcription-key"];
        public SecretSource ResolveSource(string name) => Exists(name) ? SecretSource.Environment : SecretSource.None;
    }

    private sealed class SettingsProvider(TranscriptionSettings settings) : ITranscriptionSettingsProvider
    {
        public Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
    }
}
