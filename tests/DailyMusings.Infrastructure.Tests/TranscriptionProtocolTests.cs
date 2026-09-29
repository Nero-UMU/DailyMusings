using System.Net;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The transcription module's public seam: callers always submit one recording, while the selected adapter owns
/// provider-specific URLs, payloads and response shapes.
/// </summary>
[TestClass]
public sealed class TranscriptionProtocolTests
{
    [TestMethod]
    public async Task DashScope_protocol_sends_parameters_and_reads_its_response_shape()
    {
        var handler = new RecordingHandler("""{"output":{"sentences":[{"text":"识别"},{"text":"成功"}]}}""");
        var parameters = new TranscriptionParameters(
            TranscriptionProtocolNames.DashScopeMultimodal,
            "zh,en",
            EnableItn: false,
            VocabularyId: "vocabulary-42",
            SpeakerDiarization: true,
            KeepDialect: true);
        var client = Client(handler, parameters, "qwen-audio-3.1-asr-flash");

        var result = await client.TranscribeAsync(
            new TranscriptionRequest(
                _ => Task.FromResult<Stream>(new MemoryStream("audio"u8.ToArray())),
                "capture.m4a",
                "audio/mp4"),
            CancellationToken.None);

        Assert.AreEqual("识别\n成功", result.Text);
        Assert.AreEqual(
            "https://workspace.example/api/v1/services/aigc/multimodal-generation/generation",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.AreEqual("Bearer test-key", handler.LastRequest?.Headers.Authorization?.ToString());
        Assert.IsNotNull(handler.LastRequest);
        CollectionAssert.Contains(handler.LastRequest.Headers.GetValues("X-DashScope-SSE").ToArray(), "disable");
        StringAssert.Contains(handler.LastBody, "\"format\":\"m4a\"");
        StringAssert.Contains(handler.LastBody, "\"language_hints\":[\"zh\",\"en\"]");
        StringAssert.Contains(handler.LastBody, "\"vocabulary_id\":\"vocabulary-42\"");
        StringAssert.Contains(handler.LastBody, "\"speaker_diarization_enabled\":true");
        StringAssert.Contains(handler.LastBody, "data:audio/mp4;base64,");
    }

    [TestMethod]
    public async Task Explicit_Whisper_protocol_overrides_the_model_name_heuristic()
    {
        var handler = new RecordingHandler("""{"text":"forced whisper"}""");
        var parameters = TranscriptionParameters.Default with
        {
            Protocol = TranscriptionProtocolNames.OpenAiAudioTranscriptions,
            LanguageHints = "en",
        };
        var client = Client(handler, parameters, "qwen3-asr-flash-custom-gateway");

        var result = await client.TranscribeAsync(
            new TranscriptionRequest(
                _ => Task.FromResult<Stream>(new MemoryStream("audio"u8.ToArray())),
                "capture.wav",
                "audio/wav"),
            CancellationToken.None);

        Assert.AreEqual("forced whisper", result.Text);
        Assert.AreEqual(
            "https://workspace.example/compatible-mode/v1/audio/transcriptions",
            handler.LastRequest?.RequestUri?.ToString());
        StringAssert.Contains(handler.LastBody, "name=language");
        StringAssert.Contains(handler.LastBody, "en");
    }

    private static OpenAiCompatibleTranscriptionClient Client(
        HttpMessageHandler handler,
        TranscriptionParameters parameters,
        string model) => new(
            new HttpClient(handler),
            new SecretStore(),
            new SettingsProvider(TranscriptionSettings.Default with
            {
                Enabled = true,
                BaseUrl = "https://workspace.example/compatible-mode/v1",
                Model = model,
                SecretName = "transcription-key",
                Parameters = parameters,
            }),
            NullLogger<OpenAiCompatibleTranscriptionClient>.Instance);

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

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
