using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Time;

/// <summary>
/// docs/开发指导.md §7 + decision A.4: natural days are cut in the content time zone, "late" is defined
/// only by the input's own arrival relative to its content day, and neither answer may depend on
/// whether generation has run.
/// </summary>
[TestClass]
public class ContentCalendarTests
{
    private static ContentCalendar Calendar => TestFactory.Shanghai();

    [TestMethod]
    [DataRow("2026-03-01T15:59:59Z", "2026-03-01")]
    [DataRow("2026-03-01T16:00:00Z", "2026-03-02")]
    [DataRow("2026-03-01T00:00:00Z", "2026-03-01")]
    [DataRow("2026-02-28T16:00:00Z", "2026-03-01")]
    public void Content_day_is_cut_in_the_content_time_zone(string instant, string expectedDay)
    {
        var contentDate = Calendar.ContentDateOf(DateTimeOffset.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.AreEqual(expectedDay, contentDate.ToString());
    }

    [TestMethod]
    public void Day_boundaries_are_start_and_end_of_the_content_day()
    {
        var day = TestFactory.Day(2);

        // The boundaries carry the content time zone's offset, and convert to the expected UTC instants.
        Assert.AreEqual("2026-03-02T00:00:00.0000000+08:00", Calendar.StartOfContentDay(day).ToString("o"));
        Assert.AreEqual("2026-03-02T23:59:59.9999999+08:00", Calendar.EndOfContentDay(day).ToString("o"));

        Assert.AreEqual(TestFactory.Utc(2026, 3, 1, 16, 0), Calendar.StartOfContentDay(day).ToUniversalTime());
        Assert.AreEqual(TestFactory.Utc(2026, 3, 2, 15, 59, 59).AddTicks(9_999_999), Calendar.EndOfContentDay(day).ToUniversalTime());
    }

    [TestMethod]
    public void The_last_instant_of_a_day_is_not_late_and_the_next_instant_is()
    {
        var day = TestFactory.Day(1);

        Assert.IsFalse(Calendar.IsLateArrival(day, Calendar.EndOfContentDay(day)));
        Assert.IsTrue(Calendar.IsLateArrival(day, Calendar.StartOfContentDay(day.AddDays(1))));
    }

    [TestMethod]
    public void Generation_slot_maps_to_the_configured_local_time()
    {
        // 23:00 Asia/Shanghai is 15:00 UTC.
        Assert.AreEqual(
            TestFactory.Utc(2026, 3, 1, 15, 0),
            Calendar.AtLocalTime(TestFactory.Day(1), new TimeOnly(23, 0)));
    }

    [TestMethod]
    public void Offline_capture_keeps_its_own_content_day_however_late_it_is_uploaded()
    {
        // Captured 2026-03-01 at 23:50 local, uploaded four days later: the content day is unchanged.
        var capturedAt = TestFactory.Utc(2026, 3, 1, 15, 50);
        var uploadedAt = TestFactory.Utc(2026, 3, 5, 9, 0);

        var contentDate = Calendar.ContentDateOf(capturedAt);

        Assert.AreEqual("2026-03-01", contentDate.ToString());
        Assert.IsTrue(Calendar.IsLateArrival(contentDate, uploadedAt));
    }

    [TestMethod]
    public void Late_arrival_depends_only_on_creation_and_arrival_instants()
    {
        // The same pair of instants yields the same verdict; nothing about generation state is an input
        // to the decision (A.4). This test pins the signature: IsLateArrival takes no "has generated" flag.
        var contentDate = TestFactory.Day(1);
        var arrivedInsideDay = TestFactory.Utc(2026, 3, 1, 14, 0);
        var arrivedNextDay = TestFactory.Utc(2026, 3, 1, 16, 0);

        Assert.IsFalse(Calendar.IsLateArrival(contentDate, arrivedInsideDay));
        Assert.IsTrue(Calendar.IsLateArrival(contentDate, arrivedNextDay));
    }

    [TestMethod]
    public void Day_range_is_inclusive_and_rejects_inverted_bounds()
    {
        var days = ContentDate.Range(TestFactory.Day(1), TestFactory.Day(3)).ToList();

        CollectionAssert.AreEqual(
            new[] { "2026-03-01", "2026-03-02", "2026-03-03" },
            days.Select(d => d.ToString()).ToArray());

        TestFactory.ThrowsDomain(
            "contentdate.range.inverted",
            () => ContentDate.Range(TestFactory.Day(3), TestFactory.Day(1)).ToList());
    }

    [TestMethod]
    public void Unknown_time_zone_is_rejected_at_construction()
    {
        TestFactory.ThrowsDomain("contenttimezone.unknown", () => ContentTimeZone.FromId("Mars/Olympus"));
    }

    /// <summary>An IANA id on Linux/macOS, the Windows id as a fallback, so the test runs anywhere.</summary>
    private static string? FindDstTimeZoneId()
    {
        foreach (var candidate in new[] { "America/New_York", "Eastern Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    [TestMethod]
    public void A_dst_time_zone_does_not_shift_the_content_day_boundary()
    {
        // Asia/Shanghai has no DST, so this guards against hard-coding a fixed +08:00 offset.
        var zoneId = FindDstTimeZoneId();

        if (zoneId is null)
        {
            Assert.Inconclusive("No US Eastern time zone is available on this machine.");
        }
        else
        {
            var calendar = new ContentCalendar(ContentTimeZone.FromId(zoneId));

            // 2026-03-08 is the US spring-forward day: 05:00Z is 00:00 EST, 04:00Z is still 2026-03-07.
            Assert.AreEqual("2026-03-08", calendar.ContentDateOf(TestFactory.Utc(2026, 3, 8, 5, 0)).ToString());
            Assert.AreEqual("2026-03-07", calendar.ContentDateOf(TestFactory.Utc(2026, 3, 8, 4, 0)).ToString());
        }
    }
}
