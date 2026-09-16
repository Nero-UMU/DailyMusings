using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Time;

/// <summary>
/// The configured content time zone (docs/开发指导.md §7). Defaults to <c>Asia/Shanghai</c> at the
/// composition root; the domain only ever sees a resolved, validated instance.
/// </summary>
public sealed class ContentTimeZone
{
    public const string DefaultId = "Asia/Shanghai";

    private ContentTimeZone(string id, TimeZoneInfo timeZone)
    {
        Id = id;
        TimeZoneInfo = timeZone;
    }

    public string Id { get; }

    public TimeZoneInfo TimeZoneInfo { get; }

    public static ContentTimeZone FromId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
        {
            throw new DomainException(
                "contenttimezone.unknown",
                $"Unknown content time zone '{id}'.");
        }

        return new ContentTimeZone(tz.Id, tz);
    }
}

/// <summary>
/// Turns instants into content days and answers the two time questions the rules actually depend on:
/// "which day does this input belong to?" and "did it arrive after that day had already ended?".
/// <para>
/// Every method is a pure function of its arguments — no ambient clock — so the whole of §7 is
/// unit-testable without freezing time.
/// </para>
/// </summary>
public sealed class ContentCalendar
{
    public ContentCalendar(ContentTimeZone contentTimeZone) => TimeZone = contentTimeZone;

    public ContentTimeZone TimeZone { get; }

    /// <summary>
    /// The content day of an instant, per §7: the natural day of the content time zone that contains
    /// it. <paramref name="createdOffsetMinutes"/> is deliberately NOT an input here — it is recorded
    /// on the entry for traceability only (§6.1), because letting a device's own offset pick the day
    /// would contradict "自然日按内容时区的 00:00–23:59 划分".
    /// </summary>
    public ContentDate ContentDateOf(DateTimeOffset instant) =>
        ContentDate.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone.TimeZoneInfo).DateTime);

    /// <summary>
    /// True when the input reached the server strictly after its own content day had ended in the
    /// content time zone (decision A.4). This is the sole definition of "late" in the product; it
    /// depends only on the input's creation instant, never on whether generation has run yet.
    /// </summary>
    public bool IsLateArrival(ContentDate contentDate, DateTimeOffset receivedAtUtc) =>
        ContentDateOf(receivedAtUtc) > contentDate;

    /// <summary>The last instant belonging to a content day: 23:59:59.9999999 in the content time zone.</summary>
    public DateTimeOffset EndOfContentDay(ContentDate contentDate)
    {
        var endOfDay = contentDate.Value.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(endOfDay, TimeZone.TimeZoneInfo.GetUtcOffset(endOfDay));
    }

    /// <summary>The first instant belonging to a content day: 00:00:00.0000000 in the content time zone.</summary>
    public DateTimeOffset StartOfContentDay(ContentDate contentDate)
    {
        var startOfDay = contentDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(startOfDay, TimeZone.TimeZoneInfo.GetUtcOffset(startOfDay));
    }

    /// <summary>
    /// The instant a content day's scheduled local time occurs. Used for the configurable 23:00
    /// generation and 08:00 publication slots (§7, §11.1).
    /// </summary>
    public DateTimeOffset AtLocalTime(ContentDate contentDate, TimeOnly localTime)
    {
        var local = contentDate.Value.ToDateTime(localTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZone.TimeZoneInfo.GetUtcOffset(local));
    }
}
