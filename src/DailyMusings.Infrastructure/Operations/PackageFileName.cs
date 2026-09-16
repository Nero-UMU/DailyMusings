using System.Globalization;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// The names of the packages this instance writes — readable exports and backups — and the order they were written
/// in (docs/开发指导.md §15.1, §15.2).
/// <para>
/// Both are named <c>&lt;stamp&gt;[-&lt;suffix&gt;]</c> with a one-second stamp, because that is what an operator sees on the
/// volume. Sorting them by the file system's own timestamps cannot reconstruct that order: several packages written
/// inside the same second report the same creation time, and "keep the newest seven" would then keep an arbitrary
/// seven — possibly deleting the newest archive and keeping the oldest. The name is the order the writer itself
/// handed out, so the name is what gets compared.
/// </para>
/// </summary>
internal static class PackageFileName
{
    /// <summary>The prefix backup archives carry so that a volume listing reads as this product's files.</summary>
    public const string BackupPrefix = "dailymusings-";

    /// <summary><c>yyyyMMdd-HHmmss</c>.</summary>
    private const int StampLength = 15;

    /// <summary>Splits a name into the instant it was written for and the collision suffix that disambiguates it.</summary>
    public static (string Stamp, long Suffix) Parse(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var rest = stem.StartsWith(BackupPrefix, StringComparison.Ordinal)
            ? stem[BackupPrefix.Length..]
            : stem;

        if (rest.Length <= StampLength)
        {
            return (rest, 0);
        }

        var stamp = rest[..StampLength];
        var tail = rest[(StampLength + 1)..];

        return (stamp, long.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) ? suffix : 0);
    }

    /// <summary>The items, newest first: later instant first, and within one instant the higher suffix first.</summary>
    public static IEnumerable<T> NewestFirst<T>(IEnumerable<T> items, Func<T, string> nameOf) =>
        items
            .OrderByDescending(item => Parse(nameOf(item)).Stamp, StringComparer.Ordinal)
            .ThenByDescending(item => Parse(nameOf(item)).Suffix);
}
