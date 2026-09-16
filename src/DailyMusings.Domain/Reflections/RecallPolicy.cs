using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Reflections;

/// <summary>
/// Decides which past inputs may be cited when generating a day's reflection
/// (docs/开发指导.md §8.3).
/// <para>
/// This is the code behind the single most important promise in the product: a reflection never
/// presents something as having happened on a day it did not happen. So the cutoff here is not
/// advisory — the generation request is built from this list, and nothing outside it may be cited.
/// </para>
/// </summary>
public static class RecallPolicy
{
    /// <summary>
    /// An input is recallable for a given reflection day only if the user allowed future recall, the
    /// entry still exists, it has usable text, and — the hard boundary — its content day is not in the
    /// future relative to the reflection.
    /// </summary>
    public static bool IsRecallable(InputEntry entry, ContentDate articleContentDate)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return !entry.IsDeleted
            && entry.AllowFutureRecall
            && entry.ContentDate <= articleContentDate
            && !string.IsNullOrWhiteSpace(entry.TranscriptForGeneration);
    }

    /// <summary>The recallable inputs for a day, oldest first so the prompt reads chronologically.</summary>
    public static IReadOnlyList<InputEntry> SelectRecallable(
        IEnumerable<InputEntry> candidates,
        ContentDate articleContentDate)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(entry => IsRecallable(entry, articleContentDate))
            .OrderBy(entry => entry.ContentDate)
            .ThenBy(entry => entry.CreatedAtUtc)
            .ToArray();
    }

    /// <summary>
    /// True when the cited entry predates the reflection day. Historical material must be phrased as
    /// recollection or continuation, never as a same-day event (§8.3).
    /// </summary>
    public static bool IsHistorical(InputEntry entry, ContentDate articleContentDate)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ContentDate < articleContentDate;
    }
}
