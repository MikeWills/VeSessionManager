namespace VeSessionManager.Core.FccUls;

/// <summary>
/// FCC publishes/names its daily transaction files by US Eastern calendar day (see
/// docs/fcc-uls-watcher.md), not UTC. Shared by FccUlsWatcherService (which day's file to request)
/// and FccDailyWatcherJob (when to run) so both agree on what "today" and "8pm" mean. Resolving by
/// IANA id ("America/New_York") works cross-platform since .NET 6 (ICU-backed on Windows too, not
/// just Linux/macOS) — verified directly against this repo's target framework before relying on it.
/// </summary>
public static class FccUlsSchedule
{
    public static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// The weekdays FCC actually publishes a day-name transaction file for. Files appear Tue-Sat
    /// covering Mon-Fri activity, so there is no <c>*_sun.zip</c> — requesting one is a guaranteed
    /// wasted round-trip. Used by FccUlsWatcherService.RunAllDailyFilesAsync to sweep the trailing
    /// week; ordered Mon-Sat so a sweep applies transactions oldest-first.
    /// </summary>
    public static readonly IReadOnlyList<DayOfWeek> PublishedDays =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday
    ];
}
