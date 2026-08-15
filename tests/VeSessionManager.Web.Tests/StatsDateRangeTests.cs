using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The stats page's date presets (requested 2026-08-15).
///
/// <para>The rolling ones are arithmetic and hard to get wrong. The calendar ones are the reason this
/// file exists: their boundaries are <b>Eastern</b>, and a UTC cut would be wrong for the majority of
/// this deployment's sessions — 697 of 867 start between 23:00 and 04:00 UTC, so an evening session
/// in December is stored on a January UTC date. Getting that backwards would file a slice of every
/// December into the following year while the monthly charts directly above, which already group by
/// Eastern month, disagreed.</para>
/// </summary>
public class StatsDateRangeTests
{
    /// <summary>Mid-June, so Eastern is on daylight time (UTC-4).</summary>
    private static readonly DateTime SummerNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllTime_IsUnbounded()
    {
        var (from, to) = StatsDateRanges.Resolve(StatsDateRanges.AllTimeKey, SummerNow);

        Assert.Null(from);
        Assert.Null(to);
    }

    /// <summary>An unrecognized key comes from a hand-edited query string. Show everything, don't throw.</summary>
    [Fact]
    public void AnUnknownKey_FallsBackToAllTime()
    {
        var (from, to) = StatsDateRanges.Resolve("NotARealPreset", SummerNow);

        Assert.Null(from);
        Assert.Null(to);
    }

    [Fact]
    public void NullKey_IsTreatedAsAllTime()
    {
        var (from, to) = StatsDateRanges.Resolve(null, SummerNow);

        Assert.Null(from);
        Assert.Null(to);
    }

    [Fact]
    public void RollingWindows_CountBackFromNow_AndLeaveTheEndOpen()
    {
        var (from, to) = StatsDateRanges.Resolve("Last30", SummerNow);

        Assert.Equal(SummerNow.AddDays(-30), from);
        Assert.Null(to);
    }

    /// <summary>
    /// January 1st Eastern is 05:00 UTC, not midnight — EST is UTC-5. A session run on the evening of
    /// December 31st is stored at ~01:00 UTC on January 1st, and belongs to the OLD year. A UTC-midnight
    /// boundary would pull it into the new one.
    /// </summary>
    [Fact]
    public void ThisYear_StartsAtEasternMidnight_NotUtcMidnight()
    {
        var (from, to) = StatsDateRanges.Resolve("ThisYear", SummerNow);

        Assert.Equal(new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc), from);
        Assert.Null(to);

        // The concrete case: a New Year's Eve evening session, stored on January 1st in UTC, is
        // correctly excluded from "this year".
        var newYearsEveEveningEt = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        Assert.True(newYearsEveEveningEt < from);
    }

    [Fact]
    public void LastYear_IsBoundedAtBothEnds_OnEasternBoundaries()
    {
        var (from, to) = StatsDateRanges.Resolve("LastYear", SummerNow);

        Assert.Equal(new DateTime(2025, 1, 1, 5, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc), to);
    }

    /// <summary>
    /// June is daylight time, so the month boundary is 04:00 UTC rather than 05:00. Pinned because a
    /// fixed-offset implementation would pass every winter test and be an hour wrong all summer.
    /// </summary>
    [Fact]
    public void ThisMonth_UsesTheDaylightOffsetWhenInEffect()
    {
        var (from, to) = StatsDateRanges.Resolve("ThisMonth", SummerNow);

        Assert.Equal(new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc), from);
        Assert.Null(to);
    }

    [Fact]
    public void LastMonth_EndsWhereThisMonthBegins_LeavingNoGapAndNoOverlap()
    {
        var (lastFrom, lastTo) = StatsDateRanges.Resolve("LastMonth", SummerNow);
        var (thisFrom, _) = StatsDateRanges.Resolve("ThisMonth", SummerNow);

        Assert.Equal(new DateTime(2026, 5, 1, 4, 0, 0, DateTimeKind.Utc), lastFrom);
        Assert.Equal(thisFrom, lastTo);
    }

    /// <summary>January's "last month" is December of the previous year — the arithmetic most likely to be written as a bare month-1.</summary>
    [Fact]
    public void LastMonth_InJanuary_RollsBackToDecemberOfThePreviousYear()
    {
        var januaryNow = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);

        var (from, to) = StatsDateRanges.Resolve("LastMonth", januaryNow);

        Assert.Equal(new DateTime(2025, 12, 1, 5, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc), to);
    }

    /// <summary>
    /// "Now" is late enough on December 31st Eastern that it is already January 1st in UTC. The
    /// calendar year must be read in Eastern, or on that evening every calendar preset jumps a year
    /// early — the exact class of bug CLAUDE.md records for job scheduling.
    /// </summary>
    [Fact]
    public void CalendarYear_IsReadInEastern_OnNewYearsEveEvening()
    {
        // 2026-01-01 02:00 UTC == 2025-12-31 21:00 ET. Still last year, locally.
        var newYearsEveEvening = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);

        var (from, _) = StatsDateRanges.Resolve("ThisYear", newYearsEveEvening);

        Assert.Equal(new DateTime(2025, 1, 1, 5, 0, 0, DateTimeKind.Utc), from);
    }

    [Fact]
    public void EveryOfferedOptionResolves_AndHasALabel()
    {
        foreach (var (key, label) in StatsDateRanges.Options)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.Equal(label, StatsDateRanges.LabelFor(key));

            var (from, to) = StatsDateRanges.Resolve(key, SummerNow);
            if (from is not null && to is not null)
            {
                Assert.True(from < to, $"{key} resolved to an inverted range.");
            }
        }
    }
}
