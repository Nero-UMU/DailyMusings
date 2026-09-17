using System.Net;
using System.Text;
using DailyMusings.Client.Core;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Tests;

internal sealed class FakeClock : IClientClock
{
    public FakeClock(DateTimeOffset? now = null, int localOffsetMinutes = 480)
    {
        UtcNow = now ?? new DateTimeOffset(2026, 3, 1, 15, 50, 0, TimeSpan.Zero);
        LocalOffsetMinutes = localOffsetMinutes;
    }

    public DateTimeOffset UtcNow { get; set; }

    public int LocalOffsetMinutes { get; set; }
}

/// <summary>Hands back canned responses so the HTTP layer can be exercised without a server.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public static StubHttpHandler AlwaysJson(HttpStatusCode statusCode, string json) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public static StubHttpHandler AlwaysStatus(HttpStatusCode statusCode) =>
        new(_ => new HttpResponseMessage(statusCode));

    public static StubHttpHandler Throwing(Exception exception) =>
        new(_ => throw exception);

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (request.Content is not null)
        {
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return _responder(request);
    }
}

internal sealed class StubTokenProvider : IDeviceTokenProvider
{
    public StubTokenProvider(string? token = "device-token") => Token = token;

    public string? Token { get; set; }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
}

/// <summary>
/// A recording stand-in for the server. It also observes whether the local audio still exists at the moment it is
/// asked to upload — which is how the "delete only after confirmation" ordering gets tested rather than assumed.
/// </summary>
internal sealed class FakeCaptureApiClient : ICaptureApiClient
{
    private int _nextServerId = 1;

    public List<VoiceUpload> VoiceUploads { get; } = [];

    public List<TextUpload> TextUploads { get; } = [];

    public List<bool> LocalAudioPresentAtUpload { get; } = [];

    /// <summary>Codes to fail with, consumed in order. Empty means succeed.</summary>
    public Queue<CaptureUploadException> Failures { get; } = new();

    /// <summary>
    /// Raw exceptions to throw instead, consumed in order. Used for the failures the API client does not classify —
    /// the ones a blackholed network produces — which must still end up as a retryable row.
    /// </summary>
    public Queue<Exception> Exceptions { get; } = new();

    public bool ReportAlreadyStored { get; set; }

    public Task<IngestResponse> UploadVoiceAsync(VoiceUpload upload, CancellationToken cancellationToken)
    {
        VoiceUploads.Add(upload);
        LocalAudioPresentAtUpload.Add(File.Exists(upload.LocalAudioPath));

        if (Exceptions.Count > 0)
        {
            throw Exceptions.Dequeue();
        }

        if (Failures.Count > 0)
        {
            throw Failures.Dequeue();
        }

        return Task.FromResult(BuildResponse(upload.IdempotencyKey, InputSourceNames.Voice));
    }

    public Task<IngestResponse> UploadTextAsync(TextUpload upload, CancellationToken cancellationToken)
    {
        TextUploads.Add(upload);

        if (Exceptions.Count > 0)
        {
            throw Exceptions.Dequeue();
        }

        if (Failures.Count > 0)
        {
            throw Failures.Dequeue();
        }

        return Task.FromResult(BuildResponse(upload.IdempotencyKey, InputSourceNames.Text));
    }

    public Task<InputDto?> GetInputAsync(string serverInputId, CancellationToken cancellationToken) =>
        Task.FromResult<InputDto?>(null);

    /// <summary>What the server would answer when a day is read. Defaults to "reached, nothing there".</summary>
    public InputListResult InputsResult { get; set; } = InputListResult.FromServer([]);

    public Task<InputListResult> GetInputsAsync(string contentDate, CancellationToken cancellationToken) =>
        Task.FromResult(InputsResult);

    private IngestResponse BuildResponse(string idempotencyKey, string sourceType)
    {
        var id = $"server-{_nextServerId++}";

        return new IngestResponse(
            ReportAlreadyStored,
            new InputDto(
                id,
                sourceType,
                "2026-03-01",
                "2026-03-01T15:50:00.0000000+00:00",
                480,
                sourceType == InputSourceNames.Text ? "text" : null,
                null,
                sourceType == InputSourceNames.Text ? "text" : null,
                sourceType == InputSourceNames.Text ? TranscriptionStatusNames.NotApplicable : TranscriptionStatusNames.Pending,
                null,
                sourceType == InputSourceNames.Voice,
                sourceType == InputSourceNames.Voice ? "audio/mp4" : null,
                null,
                false,
                sourceType == InputSourceNames.Voice ? JobStatusNames.Pending : null,
                0,
                null,
                []));
    }
}
