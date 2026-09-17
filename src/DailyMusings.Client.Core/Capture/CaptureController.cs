using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Capture;

/// <summary>What one pass over the queue achieved.</summary>
public sealed record SyncOutcome(int Uploaded, int Failed, int StillQueued)
{
    public static SyncOutcome Nothing { get; } = new(0, 0, 0);

    public bool AnythingHappened => Uploaded > 0 || Failed > 0;
}

/// <summary>
/// Everything the capture screens do (docs/开发指导.md §9.1, §9.2): save a capture, show the queue, push it to the
/// server, surface failures and retry them.
/// <para>
/// The ordering inside <see cref="SaveVoiceCaptureAsync"/> is the product promise: the bytes are durable before
/// this method returns, and only then does the UI say "recorded". The ordering inside <see cref="SyncAsync"/> is
/// the mirror promise: the local copy is deleted only once the server has confirmed it holds the capture.
/// </para>
/// </summary>
public sealed class CaptureController
{
    private readonly IOfflineCaptureStore _store;
    private readonly ICaptureApiClient _api;
    private readonly IClientClock _clock;

    public CaptureController(IOfflineCaptureStore store, ICaptureApiClient api, IClientClock clock)
    {
        _store = store;
        _api = api;
        _clock = clock;
    }

    /// <summary>
    /// Stores a finished recording and queues it. The audio reaches durable storage before the record is written,
    /// so a crash can never leave a queued capture whose bytes do not exist.
    /// </summary>
    public async Task<PendingCapture> SaveVoiceCaptureAsync(
        Stream audio,
        string fileExtension,
        string contentType,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var id = NewLocalId();
        var localPath = await _store
            .SaveAudioAsync(id, audio, fileExtension, cancellationToken)
            .ConfigureAwait(false);

        var capture = new PendingCapture
        {
            Id = id,
            IdempotencyKey = NewIdempotencyKey(),
            Kind = CaptureKind.Voice,
            LocalAudioPath = localPath,
            CreatedAtUtc = _clock.UtcNow,
            CreatedOffsetMinutes = _clock.LocalOffsetMinutes,
            DurationSeconds = durationSeconds,
            State = CaptureUploadState.Queued,
        };

        await _store.UpsertAsync(capture, cancellationToken).ConfigureAwait(false);
        return capture;
    }

    /// <summary>Queues a typed note. There is no audio, so nothing is written to the filesystem first.</summary>
    public async Task<PendingCapture> SaveTextCaptureAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var capture = new PendingCapture
        {
            Id = NewLocalId(),
            IdempotencyKey = NewIdempotencyKey(),
            Kind = CaptureKind.Text,
            Text = text.Trim(),
            CreatedAtUtc = _clock.UtcNow,
            CreatedOffsetMinutes = _clock.LocalOffsetMinutes,
            State = CaptureUploadState.Queued,
        };

        await _store.UpsertAsync(capture, cancellationToken).ConfigureAwait(false);
        return capture;
    }

    public Task<IReadOnlyList<PendingCapture>> GetQueueAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    public Task<PendingCapture?> FindAsync(string captureId, CancellationToken cancellationToken) =>
        _store.FindAsync(captureId, cancellationToken);

    /// <summary>
    /// Pushes every confirmed-unsent capture. One failure never stops the others: a single unsendable item must not
    /// block the rest of the day's thoughts from reaching the server.
    /// </summary>
    public async Task<SyncOutcome> SyncAsync(CancellationToken cancellationToken)
    {
        var queue = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var outstanding = queue.Where(capture => capture.HoldsOnlyCopy).ToArray();

        if (outstanding.Length == 0)
        {
            return SyncOutcome.Nothing;
        }

        var uploaded = 0;
        var failed = 0;

        foreach (var capture in outstanding)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await UploadAsync(capture, cancellationToken).ConfigureAwait(false);

                // The server has confirmed it holds this capture, so the device's copy has served its purpose.
                // Only now is deleting it allowed (§9.2).
                await _store.DeleteAsync(capture.Id, cancellationToken).ConfigureAwait(false);

                _ = response;
                uploaded++;
            }
            catch (CaptureUploadException exception)
            {
                var updated = capture with
                {
                    State = CaptureUploadState.Failed,
                    AttemptCount = capture.AttemptCount + 1,
                    FailureCode = exception.Code,
                    FailureSummary = exception.Message,
                };

                await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
                failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Anything the API client did not classify is still a failed upload, never a lost capture and never a
                // reason to take the screen down. Found on a real phone with the network path blackholed (Tailscale
                // stopped on the server's side): the HTTP stack surfaced a bare "Canceled", it escaped this method,
                // and the screen reported "操作失败：Canceled" while the row still claimed to be waiting to upload —
                // the same lesson the server's job handlers learned about unclassified exceptions.
                var updated = capture with
                {
                    State = CaptureUploadState.Failed,
                    AttemptCount = capture.AttemptCount + 1,
                    FailureCode = "client.upload_failed",
                    FailureSummary = exception.Message,
                };

                await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
                failed++;
            }
        }

        var remaining = (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

        return new SyncOutcome(uploaded, failed, remaining);
    }

    /// <summary>
    /// Clears a failure so the next sync tries again (docs/开发指导.md §9.2 手动重试). The idempotency key is
    /// deliberately left untouched: reusing it is what keeps the retry from duplicating a capture that actually
    /// landed.
    /// </summary>
    public async Task<PendingCapture?> RetryAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var capture = await _store.FindAsync(captureId, cancellationToken).ConfigureAwait(false);

        if (capture is null || capture.State == CaptureUploadState.Uploaded)
        {
            return capture;
        }

        var queued = capture with
        {
            State = CaptureUploadState.Queued,
            FailureCode = null,
            FailureSummary = null,
        };

        await _store.UpsertAsync(queued, cancellationToken).ConfigureAwait(false);
        return queued;
    }

    /// <summary>Throws a capture away, audio included. The user's explicit choice, so no confirmation logic here.</summary>
    public Task DiscardAsync(string captureId, CancellationToken cancellationToken) =>
        _store.DeleteAsync(captureId, cancellationToken);

    /// <summary>
    /// Reads the server's view of an uploaded capture, so the UI can show transcription progress without holding a
    /// second copy of that state locally.
    /// </summary>
    public async Task<InputDto?> GetServerStatusAsync(string serverInputId, CancellationToken cancellationToken) =>
        await _api.GetInputAsync(serverInputId, cancellationToken).ConfigureAwait(false);

    private async Task<IngestResponse> UploadAsync(PendingCapture capture, CancellationToken cancellationToken) =>
        capture.Kind switch
        {
            CaptureKind.Voice => await _api.UploadVoiceAsync(
                    new VoiceUpload(
                        capture.LocalAudioPath ?? throw new CaptureUploadException(
                            "client.audio_missing",
                            "The local recording is no longer on this device.",
                            transient: false),
                        ContentTypeFor(capture.LocalAudioPath),
                        capture.DurationSeconds,
                        capture.IdempotencyKey,
                        capture.CreatedAtUtc,
                        capture.CreatedOffsetMinutes),
                    cancellationToken)
                .ConfigureAwait(false),

            _ => await _api.UploadTextAsync(
                    new TextUpload(
                        capture.Text ?? string.Empty,
                        capture.IdempotencyKey,
                        capture.CreatedAtUtc,
                        capture.CreatedOffsetMinutes),
                    cancellationToken)
                .ConfigureAwait(false),
        };

    private static string ContentTypeFor(string? localAudioPath) =>
        Path.GetExtension(localAudioPath ?? string.Empty).ToLowerInvariant() switch
        {
            ".m4a" or ".mp4" => "audio/mp4",
            ".aac" => "audio/aac",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".webm" => "audio/webm",
            ".3gp" => "audio/3gpp",
            ".amr" => "audio/amr",
            _ => "application/octet-stream",
        };

    private static string NewLocalId() => Guid.CreateVersion7().ToString("N");

    private static string NewIdempotencyKey() => Guid.CreateVersion7().ToString("N");
}
