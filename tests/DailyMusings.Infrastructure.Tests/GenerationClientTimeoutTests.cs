using System.Net;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

[TestClass]
public sealed class GenerationClientTimeoutTests
{
    [TestMethod]
    public async Task Configured_timeout_also_covers_a_response_body_that_never_finishes()
    {
        var client = new OpenAiCompatibleGenerationClient(
            new HttpClient(new HeadersOnlyHandler()),
            TestSecretStore.With("test-key", "not-a-real-secret"),
            new ShortTimeoutSettings(),
            NullLogger<OpenAiCompatibleGenerationClient>.Instance);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var request = new UnsourcedCheckRequest(
            ContentDate.From(new DateOnly(2026, 9, 28)),
            "一段等待核验的正文。",
            Array.Empty<InputEntry>(),
            Array.Empty<RetrievedMaterial>());

        var failure = await Assert.ThrowsExceptionAsync<TransientExternalFailureException>(
            () => client.CheckAsync(request, watchdog.Token));

        Assert.AreEqual("generation.timeout", failure.Code);
        Assert.IsFalse(watchdog.IsCancellationRequested, "配置的 50ms 超时应先于 1s 测试看门狗生效。");
    }

    private sealed class ShortTimeoutSettings : IGenerationSettingsProvider
    {
        public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GenerationSettings.Default with
            {
                Enabled = true,
                BaseUrl = "https://generation.example/v1",
                Model = "test-model",
                SecretName = "test-key",
                Timeout = TimeSpan.FromMilliseconds(50),
            });
    }

    private sealed class HeadersOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverEndingStream()),
            });
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Only asynchronous reads are expected.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
