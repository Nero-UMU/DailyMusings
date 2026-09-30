using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Inputs;

/// <summary>
/// How long recordings are kept (docs/开发指导.md §15.1, decision A.1).
/// <para>
/// The decision this encodes is about the product's own selling point: if a recording were discarded as soon as it
/// had been transcribed, a user who later doubts the transcript would have nothing to check it against — which is
/// half of why they would trust the product at all. Keeping it forever is the other extreme, and it worsens both
/// backup size and the privacy promise. Thirty days covers "recorded today, reviewed a few days later", and the two
/// extremes stay available for people who want them.
/// </para>
/// </summary>
public sealed record AudioRetentionPolicy(int Days)
{
    /// <summary>Keep forever. Chosen explicitly, never reached by arithmetic.</summary>
    public const int KeepForever = -1;

    public static AudioRetentionPolicy Default { get; } = new(30);

    public bool KeepsForever => Days < 0;

    /// <summary>Delete as soon as the day's draft is confirmed.</summary>
    public bool DeletesImmediately => Days == 0;

    public void Validate()
    {
        if (Days < KeepForever)
        {
            throw new DomainException(
                "retention.days.out_of_range",
                "录音保留要填天数：0 = 草稿确认后立即删除，-1 = 永久保留。");
        }
    }

    /// <summary>
    /// Whether the window has elapsed since <paramref name="confirmedAtUtc"/>. The confirmation is the argument
    /// rather than something this reads from storage, because there is no single obvious instant to read: the
    /// window starts at the <em>first</em> confirmation, not at the latest change (see
    /// <see cref="Reflections.Reflection.ConfirmedAtUtc"/>).
    /// </summary>
    public bool IsDue(DateTimeOffset confirmedAtUtc, DateTimeOffset nowUtc)
    {
        Validate();

        return !KeepsForever && (DeletesImmediately || nowUtc >= confirmedAtUtc.AddDays(Days));
    }

    /// <summary>A sentence an operator can read, used by the export manifest and the admin page.</summary>
    public string Describe() => Days switch
    {
        KeepForever => "永久保留录音",
        0 => "草稿确认后立即删除录音",
        1 => "草稿确认后保留 1 天",
        _ => $"草稿确认后保留 {Days} 天",
    };
}

/// <summary>
/// Decides which recordings a cleanup sweep may physically delete
/// (docs/开发指导.md §15.1, decision A.1).
/// <para>
/// The two conditions worth naming are both about not destroying the only copy of something. Audio of a
/// <em>failed</em> transcription is never cleanable, however long it has been sitting there: the transcript does not
/// exist, so the recording is the input, and deleting it would throw away the material the retry needs. And nothing
/// is cleanable before a human has confirmed the day, because until then nobody has agreed that the text says what
/// was said.
/// </para>
/// </summary>
public static class AudioCleanupPolicy
{
    public static bool IsCleanable(
        InputEntry entry,
        DateTimeOffset? confirmedAtUtc,
        AudioRetentionPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(policy);

        if (entry.IsDeleted || !entry.HasAudio)
        {
            return false;
        }

        // A recording whose transcription never succeeded is the material a retry needs, not a leftover.
        if (entry.TranscriptionStatus != TranscriptionStatus.Succeeded)
        {
            return false;
        }

        // §15.1: 用户首次确认该日草稿后 the audio becomes a candidate. No confirmation, no cleanup.
        return confirmedAtUtc is { } confirmed && policy.IsDue(confirmed, nowUtc);
    }
}
