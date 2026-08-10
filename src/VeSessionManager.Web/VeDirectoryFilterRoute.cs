using System.Globalization;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Web;

/// <summary>
/// The VE Directory's filter state as route values, built in <b>one</b> place and read by both the
/// directory and the VE detail page it links into.
///
/// <para><b>Why it is centralised.</b> The filters have to survive a round trip — list → VE → save →
/// back to the same list — which means every link and every redirect on both pages has to carry
/// every filter. That was four <c>asp-route-*</c> attributes repeated at five sites, and it grew to
/// seven the moment license status and last-worked were added. One forgotten attribute breaks the
/// round trip silently, and only for the filter nobody remembered.</para>
///
/// <para>Values that are empty are omitted rather than written as blanks, so an unfiltered list has a
/// clean URL.</para>
/// </summary>
public static class VeDirectoryFilterRoute
{
    /// <summary>The "last worked" buckets. Stored as a key rather than resolved dates so the chosen option survives in the URL and can be re-selected in the menu.</summary>
    public const string Last3Months = "3m";
    public const string Last6Months = "6m";
    public const string LastYear = "12m";
    public const string OverAYear = "over1y";
    public const string Custom = "custom";

    public static Dictionary<string, string?> Build(
        int? teamId, string? search, string? tagName, bool includeInactive,
        WatchedLicenseStatus? licenseStatus, string? worked, DateTime? workedFrom, DateTime? workedTo)
    {
        var values = new Dictionary<string, string?>();

        if (teamId is { } team) values["teamId"] = team.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(search)) values["search"] = search;
        if (!string.IsNullOrWhiteSpace(tagName)) values["tagName"] = tagName;
        if (includeInactive) values["includeInactive"] = "true";
        if (licenseStatus is { } status) values["licenseStatus"] = status.ToString();
        if (!string.IsNullOrWhiteSpace(worked)) values["worked"] = worked;
        if (workedFrom is { } from) values["workedFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (workedTo is { } to) values["workedTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return values;
    }

    /// <summary>
    /// Turns the chosen bucket into the pair of instants the service filters on.
    ///
    /// <para><b>"Over a year" is an upper bound, not a lower one</b> — last worked on or before a year
    /// ago. It is the only option that reads as the opposite of the others, and it is the one anyone
    /// actually wants: who has gone quiet.</para>
    ///
    /// <para>Custom dates arrive as plain calendar dates from a date input, so they are anchored to
    /// Eastern midnight and Eastern end-of-day rather than UTC. Treating them as UTC would quietly
    /// drop an evening session on the boundary date — the same trap the year-boundary count had to
    /// avoid, since every session here runs after 8pm local.</para>
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) Resolve(string? worked, DateTime? customFrom, DateTime? customTo, DateTime nowUtc) =>
        worked switch
        {
            Last3Months => (nowUtc.AddMonths(-3), null),
            Last6Months => (nowUtc.AddMonths(-6), null),
            LastYear => (nowUtc.AddYears(-1), null),
            OverAYear => (null, nowUtc.AddYears(-1)),
            Custom => (EasternStartOfDayUtc(customFrom), EasternEndOfDayUtc(customTo)),
            _ => (null, null)
        };

    private static DateTime? EasternStartOfDayUtc(DateTime? date) => date is not { } d
        ? null
        : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified), UlsSchedule.EasternTimeZone);

    private static DateTime? EasternEndOfDayUtc(DateTime? date) => date is not { } d
        ? null
        : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), UlsSchedule.EasternTimeZone);

    /// <summary>Menu label for a bucket, so the trigger button and the radio list cannot describe the same option differently.</summary>
    public static string Label(string? worked) => worked switch
    {
        Last3Months => "Last 3 months",
        Last6Months => "Last 6 months",
        LastYear => "Last year",
        OverAYear => "Over a year ago",
        Custom => "Custom",
        _ => "Any"
    };
}
