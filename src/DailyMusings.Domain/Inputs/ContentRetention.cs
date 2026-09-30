using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Inputs;

/// <summary>
/// How long captured content is kept after the day's draft has been confirmed.
/// <para>
/// Separate from <see cref="AudioRetentionPolicy"/> on purpose, and deliberately off by default. The audio
/// policy answers "how long do we keep the recording so the transcript can be re-checked"; this one answers
/// "how long do we keep the material at all", which is a privacy decision the product must never take on the
/// user's behalf. Hence <c>-1</c> (keep everything) is the default, and the documented behaviour of a fresh
/// instance is unchanged: nothing is ever deleted because nobody asked for it.
/// </para>
/// </summary>
public sealed record ContentRetentionPolicy(int Days)
{
    /// <summary>Keep everything. Chosen explicitly, never reached by arithmetic.</summary>
    public const int KeepForever = -1;

    public static ContentRetentionPolicy Default { get; } = new(KeepForever);

    public bool KeepsForever => Days < 0;

    /// <summary>Delete as soon as the day's draft is confirmed.</summary>
    public bool DeletesImmediately => Days == 0;

    public void Validate()
    {
        if (Days < KeepForever)
        {
            throw new DomainException(
                "retention.days.out_of_range",
                "内容保留要填天数：0 = 草稿确认后立即删除，-1 = 永久保留。");
        }
    }

    /// <summary>
    /// Whether the window has elapsed since the <em>first</em> confirmation of the day. Keyed on that instant
    /// rather than on the content day for the same reason the audio sweep is: the window starts when a human
    /// signed the day off, not when the material happened to be recorded.
    /// </summary>
    public bool IsDue(DateTimeOffset confirmedAtUtc, DateTimeOffset nowUtc)
    {
        Validate();

        return !KeepsForever && (DeletesImmediately || nowUtc >= confirmedAtUtc.AddDays(Days));
    }

    /// <summary>A sentence an operator can read, used by the admin page and the export manifest.</summary>
    public string Describe() => Days switch
    {
        KeepForever => "永久保留随想内容",
        0 => "草稿确认后立即删除随想内容",
        1 => "草稿确认后保留随想内容 1 天",
        _ => $"草稿确认后保留随想内容 {Days} 天",
    };
}

/// <summary>
/// Decides which inputs a content sweep may strip (docs/开发指导.md §15.1 and the retention window added with
/// this feature; 计时起点见附录 A.35).
/// <para>
/// Two conditions, both about not destroying something that is still needed. The day's <em>计时起点</em> has to have
/// arrived — 起点是「首次确认」与「首次公开发布」中较早的那个，在那之前没有任何东西是可丢弃的。And an entry whose
/// transcription never succeeded is left alone: its audio is the only copy of the material, so deleting it would
/// throw away something a retry still needs (the same reasoning as <see cref="AudioCleanupPolicy"/>) — and since the
/// sweep soft-deletes the row, stripping such an entry would leave its blob unreachable rather than merely deleted.
/// Those entries are picked up by a later sweep if the transcription eventually succeeds.
/// </para>
/// </summary>
public static class ContentCleanupPolicy
{
    /// <summary>True when the entry still holds something the sweep could remove.</summary>
    public static bool HoldsContent(InputEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.HasAudio || entry.OriginalTranscript is not null || entry.RevisedTranscript is not null;
    }

    public static bool IsCleanable(
        InputEntry entry,
        DateTimeOffset? countdownFromUtc,
        ContentRetentionPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(policy);

        if (entry.IsDeleted || !HoldsContent(entry))
        {
            return false;
        }

        // A voice entry that never produced a transcript keeps its material: see the type's remarks.
        if (entry.SourceType == InputSourceType.Voice && entry.TranscriptionStatus != TranscriptionStatus.Succeeded)
        {
            return false;
        }

        return countdownFromUtc is { } from && policy.IsDue(from, nowUtc);
    }
}
