using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Inputs;

public enum InputSourceType
{
    Voice = 0,
    Text = 1,
}

public enum TranscriptionStatus
{
    /// <summary>Text entries: there is nothing to transcribe; the text <em>is</em> the transcript.</summary>
    NotApplicable = 0,
    Pending = 1,
    InProgress = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>
/// One captured thought — a short voice recording or a typed note (docs/开发指导.md §6.1).
/// </summary>
public sealed class InputEntry
{
    private readonly List<TopicId> _secondaryTopicIds = [];

    private InputEntry(
        InputEntryId id,
        InputSourceType sourceType,
        DateTimeOffset createdAtUtc,
        int createdOffsetMinutes,
        ContentDate contentDate,
        TranscriptionStatus transcriptionStatus)
    {
        Id = id;
        SourceType = sourceType;
        CreatedAtUtc = createdAtUtc;
        CreatedOffsetMinutes = createdOffsetMinutes;
        ContentDate = contentDate;
        TranscriptionStatus = transcriptionStatus;
        AllowFutureRecall = true;
    }

    public InputEntryId Id { get; }

    public InputSourceType SourceType { get; }

    /// <summary>The instant the user captured the thought. Offline queueing never rewrites this.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// The device's UTC offset at capture time. Recorded for traceability only (§7); it never decides
    /// <see cref="ContentDate"/>, which is always derived from the content time zone.
    /// </summary>
    public int CreatedOffsetMinutes { get; }

    /// <summary>Immutable once written — see decision A.5.</summary>
    public ContentDate ContentDate { get; }

    public string? AudioPath { get; private set; }

    /// <summary>MIME type of the stored blob, handed back to the transcription endpoint verbatim.</summary>
    public string? AudioContentType { get; private set; }

    public TimeSpan? AudioDuration { get; private set; }

    /// <summary>When the audio blob was physically purged. The entry and its transcript survive.</summary>
    public DateTimeOffset? AudioDeletedAtUtc { get; private set; }

    /// <summary>The verbatim model output. Never overwritten by a user edit.</summary>
    public string? OriginalTranscript { get; private set; }

    /// <summary>The user's corrected text, stored separately from <see cref="OriginalTranscript"/>.</summary>
    public string? RevisedTranscript { get; private set; }

    public TranscriptionStatus TranscriptionStatus { get; private set; }

    /// <summary>Stable code of the last transcription failure, or <c>null</c>. Never carries content (§16).</summary>
    public string? TranscriptionErrorCode { get; private set; }

    /// <summary>
    /// The client-generated key that makes a replayed upload a no-op instead of a duplicate (§9.2, §14).
    /// <c>null</c> for entries the server created on its own.
    /// </summary>
    public string? ClientIdempotencyKey { get; private set; }

    /// <summary>The paired device that captured this entry, when a client captured it.</summary>
    public DeviceId? DeviceId { get; private set; }

    /// <summary>Whether this entry may be cited as historical material in later reflections (§8.3).</summary>
    public bool AllowFutureRecall { get; private set; }

    public TopicId? PrimaryTopicId { get; private set; }

    /// <summary>At most one primary topic; secondaries are unordered and de-duplicated.</summary>
    public IReadOnlyList<TopicId> SecondaryTopicIds => _secondaryTopicIds;

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public bool IsDeleted => DeletedAtUtc is not null;

    public bool HasAudio => !string.IsNullOrEmpty(AudioPath);

    /// <summary>
    /// The text generation must use: the revision when the user made one, otherwise the original
    /// transcript (§6.1).
    /// </summary>
    public string? TranscriptForGeneration => RevisedTranscript ?? OriginalTranscript;

    public static InputEntry CreateVoice(
        InputEntryId id,
        DateTimeOffset createdAtUtc,
        int createdOffsetMinutes,
        ContentDate contentDate,
        string audioPath,
        TimeSpan? audioDuration,
        string? audioContentType = null,
        string? clientIdempotencyKey = null,
        DeviceId? deviceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ValidateOffset(createdOffsetMinutes);
        ValidateIdempotencyKey(clientIdempotencyKey);

        if (audioDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new DomainException("input.audio.negative_duration", "Audio duration cannot be negative.");
        }

        return new InputEntry(id, InputSourceType.Voice, createdAtUtc, createdOffsetMinutes, contentDate, TranscriptionStatus.Pending)
        {
            AudioPath = audioPath,
            AudioContentType = audioContentType,
            AudioDuration = audioDuration,
            ClientIdempotencyKey = clientIdempotencyKey,
            DeviceId = deviceId,
        };
    }

    public static InputEntry CreateText(
        InputEntryId id,
        DateTimeOffset createdAtUtc,
        int createdOffsetMinutes,
        ContentDate contentDate,
        string text,
        string? clientIdempotencyKey = null,
        DeviceId? deviceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateOffset(createdOffsetMinutes);
        ValidateIdempotencyKey(clientIdempotencyKey);

        return new InputEntry(id, InputSourceType.Text, createdAtUtc, createdOffsetMinutes, contentDate, TranscriptionStatus.NotApplicable)
        {
            OriginalTranscript = text.Trim(),
            ClientIdempotencyKey = clientIdempotencyKey,
            DeviceId = deviceId,
        };
    }

    /// <summary>Rehydrates a persisted entry without replaying creation rules.</summary>
    public static InputEntry Rehydrate(
        InputEntryId id,
        InputSourceType sourceType,
        DateTimeOffset createdAtUtc,
        int createdOffsetMinutes,
        ContentDate contentDate,
        string? audioPath,
        string? audioContentType,
        TimeSpan? audioDuration,
        DateTimeOffset? audioDeletedAtUtc,
        string? originalTranscript,
        string? revisedTranscript,
        TranscriptionStatus transcriptionStatus,
        string? transcriptionErrorCode,
        bool allowFutureRecall,
        TopicId? primaryTopicId,
        IEnumerable<TopicId>? secondaryTopicIds,
        DateTimeOffset? deletedAtUtc,
        string? clientIdempotencyKey,
        DeviceId? deviceId)
    {
        var entry = new InputEntry(id, sourceType, createdAtUtc, createdOffsetMinutes, contentDate, transcriptionStatus)
        {
            AudioPath = audioPath,
            AudioContentType = audioContentType,
            AudioDuration = audioDuration,
            AudioDeletedAtUtc = audioDeletedAtUtc,
            OriginalTranscript = originalTranscript,
            RevisedTranscript = revisedTranscript,
            TranscriptionErrorCode = transcriptionErrorCode,
            AllowFutureRecall = allowFutureRecall,
            PrimaryTopicId = primaryTopicId,
            DeletedAtUtc = deletedAtUtc,
            ClientIdempotencyKey = clientIdempotencyKey,
            DeviceId = deviceId,
        };

        if (secondaryTopicIds is not null)
        {
            entry._secondaryTopicIds.AddRange(secondaryTopicIds.Where(topicId => !topicId.IsEmpty).Distinct());
        }

        return entry;
    }

    public void BeginTranscription()
    {
        EnsureNotDeleted();
        if (SourceType != InputSourceType.Voice)
        {
            throw new DomainException("input.transcription.not_voice", "Only voice entries are transcribed.");
        }

        if (TranscriptionStatus is not (TranscriptionStatus.Pending or TranscriptionStatus.Failed))
        {
            throw new DomainException(
                "input.transcription.bad_state",
                $"Cannot start transcription while status is {TranscriptionStatus}.");
        }

        TranscriptionStatus = TranscriptionStatus.InProgress;
    }

    public void CompleteTranscription(string originalTranscript)
    {
        EnsureNotDeleted();
        ArgumentException.ThrowIfNullOrWhiteSpace(originalTranscript);

        if (TranscriptionStatus != TranscriptionStatus.InProgress)
        {
            throw new DomainException(
                "input.transcription.bad_state",
                $"Cannot complete transcription while status is {TranscriptionStatus}.");
        }

        OriginalTranscript = originalTranscript.Trim();
        TranscriptionStatus = TranscriptionStatus.Succeeded;
        TranscriptionErrorCode = null;
    }

    /// <summary>Records a transcription failure. The entry and its audio are always kept (§20).</summary>
    public void FailTranscription(string? errorCode = null)
    {
        EnsureNotDeleted();
        if (TranscriptionStatus == TranscriptionStatus.Succeeded)
        {
            throw new DomainException(
                "input.transcription.bad_state",
                "A succeeded transcription cannot be failed; revise the transcript instead.");
        }

        TranscriptionStatus = TranscriptionStatus.Failed;
        TranscriptionErrorCode = errorCode;
    }

    /// <summary>
    /// Puts a failed — or stuck — transcription back in the queue.
    /// <para>
    /// <see cref="TranscriptionStatus.InProgress"/> is accepted on purpose. Found by running the real pipeline: an
    /// internal error after the attempt had started left the entry in progress forever, and refusing to retry it
    /// turned one defect into a permanently unusable capture. Re-running a transcription is safe — the audio is
    /// still there and the job is the same — so being generous here costs nothing and rescues the entry.
    /// </para>
    /// </summary>
    public void RetryTranscription()
    {
        EnsureNotDeleted();
        if (TranscriptionStatus is not (TranscriptionStatus.Failed or TranscriptionStatus.InProgress))
        {
            throw new DomainException(
                "input.transcription.bad_state",
                $"Only a failed or stuck transcription can be retried (status is {TranscriptionStatus}).");
        }

        TranscriptionStatus = TranscriptionStatus.Pending;
        TranscriptionErrorCode = null;
    }

    /// <summary>
    /// Stores the user's correction. Passing blank clears the revision so generation falls back to the
    /// original transcript again.
    /// </summary>
    public void ReviseTranscript(string? revisedTranscript)
    {
        EnsureNotDeleted();
        if (TranscriptionStatus != TranscriptionStatus.Succeeded)
        {
            throw new DomainException(
                "input.transcript.not_ready",
                "A transcript can only be revised after transcription succeeded.");
        }

        RevisedTranscript = string.IsNullOrWhiteSpace(revisedTranscript) ? null : revisedTranscript.Trim();
    }

    /// <summary>
    /// Purges only the audio blob. The entry, its transcripts and its source-mapping role all survive.
    /// <para>
    /// Two other operations also touch the audio but mean something else entirely: the retention sweep clears it
    /// together with the text (<c>PurgeContent</c>), and a manual delete removes the whole row. This one leaves
    /// the entry usable, so a retry or a later revision still works (§6.1, §15.1, §17.1).
    /// </para>
    /// </summary>
    /// <returns>The audio path the caller must delete from storage, or <c>null</c> if there was none.</returns>
    public string? DeleteAudio(DateTimeOffset at)
    {
        if (!HasAudio)
        {
            return null; // idempotent: safe to retry a DELETE /api/inputs/{id}/audio call
        }

        var path = AudioPath;
        AudioPath = null;
        AudioDeletedAtUtc = at;
        return path;
    }

    /// <summary>
    /// Strips everything the content-retention sweep removes and leaves the row in place as a tombstone.
    /// <para>
    /// A soft delete rather than a real one, and that is the whole point of doing it here rather than in SQL:
    /// <c>source_reference</c> rows point at this entry, so removing the row would leave a historical article's
    /// provenance dangling (and the schema would refuse it). The entry therefore keeps its identity, its content
    /// day and its place in the source map, while the text and the recording are gone for good — which is
    /// exactly what the user asked the retention window to mean.
    /// </para>
    /// <para>
    /// This is the <em>only</em> transition that sets the tombstone. A user-initiated delete does not come through
    /// here: it removes the input row and its source links outright (§15.1), because the user asked for the entry
    /// to be gone rather than for its content to expire.
    /// </para>
    /// </summary>
    /// <returns>The audio path the caller must delete from storage, or <c>null</c> if there was none.</returns>
    public string? PurgeContent(DateTimeOffset at)
    {
        if (IsDeleted)
        {
            return null; // idempotent: a second sweep over the same day must not behave differently
        }

        var path = AudioPath;

        AudioPath = null;
        AudioDuration = null;
        AudioDeletedAtUtc = path is null ? AudioDeletedAtUtc : at;
        OriginalTranscript = null;
        RevisedTranscript = null;
        DeletedAtUtc = at;

        return path;
    }

    public void SetAllowFutureRecall(bool allow)
    {
        EnsureNotDeleted();
        AllowFutureRecall = allow;
    }

    /// <summary>
    /// Assigns topics. Enforces the §6.2 invariant that an input has at most one primary topic and that
    /// the primary is never also listed as a secondary.
    /// </summary>
    public void AssignTopics(TopicId? primaryTopicId, IEnumerable<TopicId>? secondaryTopicIds)
    {
        EnsureNotDeleted();

        var secondary = (secondaryTopicIds ?? []).Where(id => !id.IsEmpty).Distinct().ToList();

        if (primaryTopicId is { IsEmpty: false } primary && secondary.Contains(primary))
        {
            throw new DomainException(
                "input.topics.primary_also_secondary",
                "The primary topic cannot also appear in the secondary topics.");
        }

        PrimaryTopicId = primaryTopicId is { IsEmpty: false } ? primaryTopicId : null;

        _secondaryTopicIds.Clear();
        _secondaryTopicIds.AddRange(secondary);
    }

    /// <summary>Re-points every topic assignment away from a topic that was merged into another (A.9).</summary>
    public void RemapTopic(TopicId from, TopicId to)
    {
        if (PrimaryTopicId == from)
        {
            PrimaryTopicId = to;
        }

        for (var i = 0; i < _secondaryTopicIds.Count; i++)
        {
            if (_secondaryTopicIds[i] == from)
            {
                _secondaryTopicIds[i] = to;
            }
        }

        var deduped = _secondaryTopicIds.Distinct().ToList();
        if (PrimaryTopicId is { } primary)
        {
            deduped.Remove(primary);
        }

        _secondaryTopicIds.Clear();
        _secondaryTopicIds.AddRange(deduped);
    }

    private static void ValidateOffset(int createdOffsetMinutes)
    {
        if (createdOffsetMinutes is < -840 or > 840)
        {
            throw new DomainException(
                "input.offset.out_of_range",
                $"UTC offset {createdOffsetMinutes} minutes is outside the real-world range of ±14 hours.");
        }
    }

    /// <summary>
    /// A client key must be short and opaque. Bounded because it comes from an untrusted client and is stored;
    /// a client that wants a longer identifier can hash it.
    /// </summary>
    private static void ValidateIdempotencyKey(string? clientIdempotencyKey)
    {
        if (clientIdempotencyKey is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(clientIdempotencyKey) || clientIdempotencyKey.Length > 128)
        {
            throw new DomainException(
                "input.idempotency_key.invalid",
                "A client idempotency key must be between 1 and 128 characters.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("input.deleted", "The input has been deleted.");
        }
    }
}
