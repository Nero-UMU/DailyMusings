using System.Globalization;
using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Time;

/// <summary>
/// The natural day (in the configured content time zone) that an input or a reflection belongs to.
/// <para>
/// Kept as a distinct type rather than a bare <see cref="DateOnly"/> because mixing a content day up
/// with any other date is exactly the mistake the time rules in docs/开发指导.md §7 exist to prevent.
/// </para>
/// <para>
/// Immutability is deliberate: §7 (and decision A.5) require that a content day, once written,
/// is never recomputed — not when the content time zone changes, and not when an input is uploaded
/// late. Everything that derives from it (one-reflection-per-day, backfill eligibility, the recall
/// cutoff) depends on that stability.
/// </para>
/// </summary>
public readonly record struct ContentDate : IComparable<ContentDate>
{
    private ContentDate(DateOnly value) => Value = value;

    public DateOnly Value { get; }

    public static ContentDate From(DateOnly value) => new(value);

    public static ContentDate FromDateTime(DateTime value) => new(DateOnly.FromDateTime(value));

    public static ContentDate Parse(string text) =>
        new(DateOnly.ParseExact(text, Format, CultureInfo.InvariantCulture));

    public static bool TryParse(string? text, out ContentDate result)
    {
        if (DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            result = new ContentDate(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public const string Format = "yyyy-MM-dd";

    public ContentDate AddDays(int days) => new(Value.AddDays(days));

    public int CompareTo(ContentDate other) => Value.CompareTo(other.Value);

    public static bool operator <(ContentDate left, ContentDate right) => left.Value < right.Value;

    public static bool operator <=(ContentDate left, ContentDate right) => left.Value <= right.Value;

    public static bool operator >(ContentDate left, ContentDate right) => left.Value > right.Value;

    public static bool operator >=(ContentDate left, ContentDate right) => left.Value >= right.Value;

    /// <summary>Inclusive day range, used by backfill (§7: 按每条输入的实际创建日期逐日补跑).</summary>
    public static IEnumerable<ContentDate> Range(ContentDate fromInclusive, ContentDate toInclusive)
    {
        if (fromInclusive > toInclusive)
        {
            throw new DomainException(
                "contentdate.range.inverted",
                $"Range start {fromInclusive} is after range end {toInclusive}.");
        }

        for (var day = fromInclusive; day <= toInclusive; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    public override string ToString() => Value.ToString(Format, CultureInfo.InvariantCulture);
}
