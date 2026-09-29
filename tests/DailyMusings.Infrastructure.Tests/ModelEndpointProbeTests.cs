using System.Net;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

[TestClass]
public sealed class ModelEndpointProbeTests
{
    [TestMethod]
    public async Task Candidate_probe_uses_the_unsaved_form_url_and_key()
    {
        var handler = new RecordingHandler();
        var probe = CreateProbe(handler);

        var result = await probe.ProbeModelAsync(
            new ModelEndpointProbeRequest(
                ExternalService.Generation,
                "https://candidate.example/v1",
                "candidate-model",
                "saved-key",
                "typed-key"),
            CancellationToken.None);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual("https://candidate.example/v1/chat/completions", handler.LastRequest?.RequestUri?.ToString());
        Assert.AreEqual("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.AreEqual("typed-key", handler.LastRequest?.Headers.Authorization?.Parameter);
    }

    [TestMethod]
    public async Task Candidate_probe_rejects_invalid_urls_without_using_the_saved_endpoint()
    {
        var handler = new RecordingHandler();
        var probe = CreateProbe(handler);

        var result = await probe.ProbeModelAsync(
            new ModelEndpointProbeRequest(ExternalService.Generation, "not-a-url", "candidate-model", "saved-key"),
            CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("probe.generation.url_invalid", result.Code);
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    public async Task Transcription_probe_calls_the_configured_ASR_contract_instead_of_only_listing_models()
    {
        var handler = new RecordingHandler();
        var probe = CreateProbe(handler);

        var result = await probe.ProbeModelAsync(
            new ModelEndpointProbeRequest(
                ExternalService.Transcription,
                "https://candidate.example/v1",
                "arbitrary-model",
                "saved-key",
                "typed-key",
                TranscriptionApiTypes.OpenAiChatAudio),
            CancellationToken.None);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual("https://candidate.example/v1/chat/completions", handler.LastRequest?.RequestUri?.ToString());
        StringAssert.Contains(handler.LastBody, "arbitrary-model");
        StringAssert.Contains(handler.LastBody, "\"type\":\"input_audio\"");
    }

    [TestMethod]
    public async Task DashScope_probe_reports_that_the_synthetic_local_recording_has_no_public_url()
    {
        var handler = new RecordingHandler();
        var probe = CreateProbe(handler);

        var result = await probe.ProbeModelAsync(
            new ModelEndpointProbeRequest(
                ExternalService.Transcription,
                "https://workspace.example/api/v1",
                "any-model",
                "saved-key",
                "typed-key",
                TranscriptionApiTypes.DashScopeAsync),
            CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("probe.transcription.public_audio_url_required", result.Code);
        Assert.IsNull(handler.LastRequest);
    }

    private static ExternalServiceProbe CreateProbe(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new SecretStore(),
        new StaticTranscriptionSettings(),
        new StaticGenerationSettings(),
        new StaticEmbeddingSettings(),
        new StaticSmtpSettings(),
        NullLogger<ExternalServiceProbe>.Instance);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SecretStore : ISecretStore
    {
        public string? TryGet(string name) => name == "saved-key" ? "stored-key" : null;
        public bool Exists(string name) => TryGet(name) is not null;
        public IReadOnlyList<string> ListNames() => ["saved-key"];
        public SecretSource ResolveSource(string name) => Exists(name) ? SecretSource.Ui : SecretSource.None;
    }

    private sealed class StaticTranscriptionSettings : ITranscriptionSettingsProvider
    {
        public Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TranscriptionSettings.Default);
    }

    private sealed class StaticGenerationSettings : IGenerationSettingsProvider
    {
        public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GenerationSettings.Default with
            {
                Enabled = true,
                BaseUrl = "https://saved.example/v1",
                SecretName = "saved-key",
            });
    }

    private sealed class StaticEmbeddingSettings : IEmbeddingSettingsProvider
    {
        public Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(EmbeddingSettings.Default);
    }

    private sealed class StaticSmtpSettings : ISmtpSettingsProvider
    {
        public Task<SmtpSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(SmtpSettings.Default);
    }
}
