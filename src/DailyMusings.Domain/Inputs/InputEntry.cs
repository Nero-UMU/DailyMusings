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

    public TimeSpan? AudioDuration { get; private set; }

    /// <summary>When the audio blob was physically purged. The entry and its transcript survive.</summary>
    public DateTimeOffset? AudioDeletedAtUtc { get; private set; }

    /// <summary>The verbatim model output. Never overwritten by a user edit.</summary>
    public string? OriginalTranscript { get; private set; }

    /// <summary>The user's corrected text, stored separately from <see cref="OriginalTranscript"/>.</summary>
    public string? RevisedTranscript { get; private set; }

    public TranscriptionStatus TranscriptionStatus { get; private set; }

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
        TimeSpan? audioDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ValidateOffset(createdOffsetMinutes);

        if (audioDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new DomainException("input.audio.negative_duration", "Audio duration cannot be negative.");
        }

        return new InputEntry(id, InputSourceType.Voice, createdAtUtc, createdOffsetMinutes, contentDate, TranscriptionStatus.Pending)
        {
            AudioPath = audioPath,
            AudioDuration = audioDuration,
        };
    }

    public static InputEntry CreateText(
        InputEntryId id,
        DateTimeOffset createdAtUtc,
        int createdOffsetMinutes,
        ContentDate contentDate,
        string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateOffset(createdOffsetMinutes);

        return new InputEntry(id, InputSourceType.Text, createdAtUtc, createdOffsetMinutes, contentDate, TranscriptionStatus.NotApplicable)
        {
            OriginalTranscript = text.Trim(),
        };
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
    }

    /// <summary>Records a transcription failure. The entry and its audio are always kept (§20).</summary>
    public void FailTranscription()
    {
        EnsureNotDeleted();
        if (TranscriptionStatus == TranscriptionStatus.Succeeded)
        {
            throw new DomainException(
                "input.transcription.bad_state",
                "A succeeded transcription cannot be failed; revise the transcript instead.");
        }

        TranscriptionStatus = TranscriptionStatus.Failed;
    }

    /// <summary>Puts a failed transcription back in the queue.</summary>
    public void RetryTranscription()
    {
        EnsureNotDeleted();
        if (TranscriptionStatus != TranscriptionStatus.Failed)
        {
            throw new DomainException(
                "input.transcription.bad_state",
                $"Only failed transcriptions can be retried (status is {TranscriptionStatus}).");
        }

        TranscriptionStatus = TranscriptionStatus.Pending;
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
    /// Purges only the audio blob. The entry, its transcripts and its source-mapping role all survive —
    /// this is deliberately different from <see cref="Delete"/> (§17.1).
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
    /// Soft-deletes the whole entry and detaches its audio. The caller is responsible for purging the
    /// returned blob path, so that record state and blob storage cannot drift apart.
    /// </summary>
    /// <returns>The audio path the caller must delete from storage, or <c>null</c> if there was none.</returns>
    public string? Delete(DateTimeOffset at)
    {
        if (IsDeleted)
        {
            return null; // idempotent
        }

        var path = AudioPath;
        DeletedAtUtc = at;
        AudioPath = null;
        AudioDuration = null;
        AudioDeletedAtUtc = path is null ? AudioDeletedAtUtc : at;
        return path;
    }

    /// <summary>Restores a soft-deleted entry (the audio cannot come back).</summary>
    public void Restore()
    {
        if (IsDeleted)
        {
            DeletedAtUtc = null;
        }
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

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("input.deleted", "The input has been deleted.");
        }
    }
}
