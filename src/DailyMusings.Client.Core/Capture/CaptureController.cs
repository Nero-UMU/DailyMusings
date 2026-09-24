using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Capture;

/// <summary>What one pass over the not-yet-uploaded records achieved.</summary>
public sealed record SyncOutcome(int Uploaded, int Failed, int StillQueued)
{
    public static SyncOutcome Nothing { get; } = new(0, 0, 0);

    public bool AnythingHappened => Uploaded > 0 || Failed > 0;
}

/// <summary>The result of pushing one record to the server, classified rather than thrown.</summary>
public sealed record UploadOutcome(
    string CaptureId,
    bool Uploaded,
    string? Transcript,
    string? FailureCode,
    string? FailureSummary);

/// <summary>How much of the device the local archive is using.</summary>
public sealed record LocalUsage(int Count, long AudioBytes, int VoiceCount);

/// <summary>
/// Everything the phone's screens do with a thought (docs/开发指导.md §9.1, §9.2): save it, show it, push it to the
/// server, and read it back from the local archive.
/// <para>
/// The ordering inside <see cref="SaveVoiceCaptureAsync"/> is the product promise: the bytes are durable before this
/// method returns, and only then does the UI say "recorded". The promise at the other end has changed with the
/// phone's narrower scope — a successful upload <em>keeps</em> the local copy, because the device is now the source
/// of truth for 往期记录; nothing here deletes a capture except
/// <see cref="DeleteLocalAsync"/> and <see cref="ClearLocalAsync"/>, which the user asked for.
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

    /// <summary>The local archive, newest first — what the calendar lists.</summary>
    public Task<IReadOnlyList<PendingCapture>> ListLocalAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    public Task<PendingCapture?> FindAsync(string captureId, CancellationToken cancellationToken) =>
        _store.FindAsync(captureId, cancellationToken);

    /// <summary>
    /// Pushes one recorded thought to the server, keeping the local copy either way.
    /// <para>
    /// Never throws for a failed upload: every outcome — transport failure, refusal, an exception the API client did
    /// not classify — comes back as a failure with a code and a sentence. A capture that is already durable on the
    /// device must not take the screen down (§9.2), which is a lesson a real phone taught this codebase twice.
    /// </para>
    /// </summary>
    public async Task<UploadOutcome> UploadAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var capture = await _store.FindAsync(captureId, cancellationToken).ConfigureAwait(false);

        if (capture is null)
        {
            return new UploadOutcome(captureId, false, null, "client.capture_missing", "本机没有这条记录。");
        }

        try
        {
            var response = await SendAsync(capture, cancellationToken).ConfigureAwait(false);

            // The upload answers with the entry it created, and the server transcribes voice inline by default, so
            // the recognised text is usually right here. When it is not (a slow model, transcription switched off),
            // the text arrives later through RefreshFromServerAsync.
            var transcript = response.Input.Transcript ?? response.Input.OriginalTranscript;

            var updated = capture with
            {
                State = CaptureUploadState.Uploaded,
                ServerInputId = response.Input.Id,
                Transcript = transcript,
                UploadedAtUtc = _clock.UtcNow,
                TranscriptionAtUtc = string.IsNullOrWhiteSpace(transcript) ? null : _clock.UtcNow,
                FailureCode = null,
                FailureSummary = null,
            };

            await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);

            return new UploadOutcome(captureId, true, transcript, null, null);
        }
        catch (CaptureUploadException exception)
        {
            await MarkFailedAsync(capture, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
            return new UploadOutcome(captureId, false, null, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Anything the API client did not classify is still a failed upload, never a lost capture and never a
            // reason to take the screen down. Found on a real phone with the network path blackholed (Tailscale
            // stopped on the server's side): the HTTP stack surfaced a bare "Canceled", it escaped this method, and
            // the screen reported "操作失败：Canceled" while the row still claimed to be waiting to upload — the
            // same lesson the server's job handlers learned about unclassified exceptions.
            await MarkFailedAsync(capture, "client.upload_failed", exception.Message, cancellationToken).ConfigureAwait(false);
            return new UploadOutcome(captureId, false, null, "client.upload_failed", exception.Message);
        }
    }

    /// <summary>
    /// Pushes everything the server has not confirmed, oldest first — the user's thoughts should reach the server in
    /// the order they were had. One failure never stops the others: a single unsendable item must not block the rest
    /// of the day's thoughts. Nothing is deleted here (§9.2, and the phone's local archive).
    /// </summary>
    public async Task<SyncOutcome> SyncAsync(CancellationToken cancellationToken)
    {
        var queue = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var outstanding = queue
            .Where(capture => capture.NeedsUpload)
            .OrderBy(capture => capture.CreatedAtUtc)
            .ToArray();

        if (outstanding.Length == 0)
        {
            return SyncOutcome.Nothing;
        }

        var uploaded = 0;
        var failed = 0;

        foreach (var capture in outstanding)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await UploadAsync(capture.Id, cancellationToken).ConfigureAwait(false);

            if (outcome.Uploaded)
            {
                uploaded++;
            }
            else
            {
                failed++;
            }
        }

        var remaining = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Count(capture => capture.NeedsUpload);

        return new SyncOutcome(uploaded, failed, remaining);
    }

    /// <summary>
    /// Deletes one record and its audio, because the user asked to (§3.2's 删除本次录音). Returns the record that was
    /// removed, or <c>null</c> when there was nothing to delete.
    /// </summary>
    public async Task<PendingCapture?> DeleteLocalAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var capture = await _store.FindAsync(captureId, cancellationToken).ConfigureAwait(false);

        if (capture is null)
        {
            return null;
        }

        await _store.DeleteAsync(captureId, cancellationToken).ConfigureAwait(false);
        return capture;
    }

    /// <summary>Empties the device's archive: every record and every recording. Only ever called behind a
    /// confirmation, because this is the one operation that can destroy a thought the server never got.</summary>
    public async Task<int> ClearLocalAsync(CancellationToken cancellationToken)
    {
        var captures = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var capture in captures)
        {
            await _store.DeleteAsync(capture.Id, cancellationToken).ConfigureAwait(false);
        }

        return captures.Count;
    }

    /// <summary>How many records and how many bytes of audio the archive is holding, for the settings screen.</summary>
    public async Task<LocalUsage> GetLocalUsageAsync(CancellationToken cancellationToken)
    {
        var captures = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        long bytes = 0;
        var voices = 0;

        foreach (var capture in captures)
        {
            if (!capture.IsVoice)
            {
                continue;
            }

            voices++;

            if (capture.LocalAudioPath is { Length: > 0 } path && File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
            }
        }

        return new LocalUsage(captures.Count, bytes, voices);
    }

    /// <summary>
    /// Asks the server about one uploaded record and writes the recognised text back if it has arrived. This is the
    /// second half of the inline-transcription contract: an upload that answered with <c>pending</c> gets its text a
    /// moment later, and the phone's archive has to end up holding it.
    /// <para>
    /// A transport failure leaves the record untouched and is not an error worth a dialog — the text will be picked
    /// up by the next refresh, and the recording itself is already safe on both sides.
    /// </para>
    /// </summary>
    public async Task<PendingCapture?> RefreshFromServerAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var capture = await _store.FindAsync(captureId, cancellationToken).ConfigureAwait(false);

        if (capture?.ServerInputId is not { Length: > 0 } serverInputId)
        {
            return capture;
        }

        if (!string.IsNullOrWhiteSpace(capture.Transcript))
        {
            // Nothing to fill in: the upload response already carried the text.
            return capture;
        }

        InputDto? entry;

        try
        {
            entry = await _api.GetInputAsync(serverInputId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return capture;
        }

        var transcript = entry?.Transcript ?? entry?.OriginalTranscript;

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return capture;
        }

        var updated = capture with
        {
            Transcript = transcript,
            TranscriptionAtUtc = _clock.UtcNow,
        };

        await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Fills in the text of every uploaded recording that has none, in one read. Used when the calendar opens: a
    /// phone that was closed before the model answered would otherwise show a pile of recordings with no words.
    /// A server that cannot be reached changes nothing and says nothing — the local archive is still complete.
    /// </summary>
    public async Task<int> RefreshMissingTranscriptsAsync(CancellationToken cancellationToken)
    {
        var pending = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(capture => capture.IsVoice
                && capture.ServerInputId is { Length: > 0 }
                && string.IsNullOrWhiteSpace(capture.Transcript))
            .ToArray();

        if (pending.Length == 0)
        {
            return 0;
        }

        var recent = await _api.GetInputsAsync(contentDate: null, cancellationToken).ConfigureAwait(false);

        if (!recent.Succeeded)
        {
            return 0;
        }

        var byId = (recent.Value ?? []).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var updated = 0;

        foreach (var capture in pending)
        {
            if (!byId.TryGetValue(capture.ServerInputId!, out var entry))
            {
                continue;
            }

            var transcript = entry.Transcript ?? entry.OriginalTranscript;

            if (string.IsNullOrWhiteSpace(transcript))
            {
                continue;
            }

            await _store
                .UpsertAsync(
                    capture with { Transcript = transcript, TranscriptionAtUtc = _clock.UtcNow },
                    cancellationToken)
                .ConfigureAwait(false);

            updated++;
        }

        return updated;
    }

    private async Task MarkFailedAsync(
        PendingCapture capture,
        string code,
        string summary,
        CancellationToken cancellationToken)
    {
        var updated = capture with
        {
            State = CaptureUploadState.Failed,
            AttemptCount = capture.AttemptCount + 1,
            FailureCode = code,
            FailureSummary = summary,
        };

        await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestResponse> SendAsync(PendingCapture capture, CancellationToken cancellationToken) =>
        capture.Kind switch
        {
            // Note the absent transcribeNow field: the server's default is "recognise now, while the upload waits",
            // which is what the phone wants. Sending transcribeNow=false would turn this back into a two-step flow
            // for no reason (the phone spec's §0.1).
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
