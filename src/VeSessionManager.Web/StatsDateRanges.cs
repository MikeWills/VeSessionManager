using VeSessionManager.Core.Uls;

namespace VeSessionManager.Web;

/// <summary>
/// The date ranges the stats page offers, and the one place each one's boundaries are defined.
///
/// <para>Separate from <c>IndexModel.DateRangePresets</c>, which the session list and VE Roster
/// share, because those are all <i>rolling</i> windows ("last 30 days") expressed as a day count.
/// That shape cannot express a calendar range: "this year" needs both ends, and its start is a
/// fixed date rather than an offset from now. Rather than bolt a second meaning onto a
/// <c>(int Days, string Label)</c> tuple used by two other pages, the stats page gets its own
/// definition covering both kinds. Stats.cshtml.cs's remarks about mirroring VeRoster's controls
/// still hold for the team picker and the custom from/to inputs — this is the one divergence, and it
/// exists because the question the stats page answers is naturally a calendar one ("how did last
/// year go?") while a session worklist's is naturally rolling.</para>
///
/// <para><b>Calendar boundaries are Eastern, not UTC</b>, for the reason recorded throughout this
/// codebase: 697 of 867 stored sessions start between 23:00 and 04:00 UTC, so an evening session in
/// December is stored on a January UTC date. Cutting "this year" on UTC midnight would file a chunk
/// of every December into the following year and quietly disagree with the monthly charts directly
/// above it, which already group by Eastern month.</para>
/// </summary>
public static class StatsDateRanges
{
    /// <summary>The key that means "no filter". Empty string so an absent query parameter and an explicit "all time" are the same thing.</summary>
    public const string AllTimeKey = "";

    public const string DefaultKey = AllTimeKey;

    /// <summary>Ordered as rendered. Rolling windows first, then calendar ones — shortest to longest within each group.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Options =
    [
        (AllTimeKey, "All time"),
        ("Last30", "Last 30 days"),
        ("Last90", "Last 90 days"),
        ("ThisMonth", "This month"),
        ("LastMonth", "Last month"),
        ("ThisYear", "This year"),
        ("LastYear", "Last year")
    ];

    public static string LabelFor(string? key) =>
        Options.FirstOrDefault(o => o.Key == (key ?? AllTimeKey)).Label ?? "All time";

    /// <summary>
    /// Resolves a key to a UTC half-open-ish range. Null on either side means unbounded, matching what
    /// <c>SessionStatsService.GetAsync</c> already accepts.
    ///
    /// <para>An unrecognized key resolves to all time rather than throwing — the value arrives from a
    /// query string, and a hand-edited one should show everything, not a 500.</para>
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) Resolve(string? key, DateTime nowUtc)
    {
        var easternNow = UlsSchedule.ToEastern(nowUtc);

        switch (key)
        {
            case "Last30":
                return (nowUtc.AddDays(-30), null);
            case "Last90":
                return (nowUtc.AddDays(-90), null);

            case "ThisMonth":
                return (EasternStartOfMonth(easternNow.Year, easternNow.Month), null);

            case "LastMonth":
            {
                var firstOfThisMonth = new DateTime(easternNow.Year, easternNow.Month, 1);
                var firstOfLastMonth = firstOfThisMonth.AddMonths(-1);
                return (EasternToUtc(firstOfLastMonth), EasternToUtc(firstOfThisMonth));
            }

            case "ThisYear":
                return (EasternToUtc(new DateTime(easternNow.Year, 1, 1)), null);

            case "LastYear":
                return (EasternToUtc(new DateTime(easternNow.Year - 1, 1, 1)),
                        EasternToUtc(new DateTime(easternNow.Year, 1, 1)));

            default:
                return (null, null);
        }
    }

    private static DateTime EasternStartOfMonth(int year, int month) =>
        EasternToUtc(new DateTime(year, month, 1));

    /// <summary>
    /// An Eastern wall-clock instant as UTC.
    /// </summary>
    /// <remarks>
    /// Midnight is never a DST-invalid time in US Eastern — the spring-forward gap is 02:00-03:00 —
    /// so no <c>IsInvalidTime</c> dance is needed here, unlike <c>DailySlotSchedule</c>, which places
    /// slots at arbitrary hours and does have to handle it. Stated rather than left implicit because
    /// the absence of that check is the kind of thing that reads as an oversight.
    /// </remarks>
    private static DateTime EasternToUtc(DateTime easternWallClock) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(easternWallClock, DateTimeKind.Unspecified), UlsSchedule.EasternTimeZone);
}
