namespace VeSessionManager.Core.Uls;

/// <summary>
/// Wall-clock Eastern scheduling for jobs that must run at a stated time of day rather than "every
/// N hours from whenever the Worker started".
///
/// <para><b>Why anchor at all.</b> FCC posts its daily changes at 02:00 ET. For a job that reads
/// that data, the useful question is not how often to poll but how soon after that nightly run it
/// looks — and an unanchored timer answers "somewhere between 0 and N hours, depending on when the
/// service last restarted". A renewal granted at 02:00 ET on 2026-08-06 was still invisible that
/// morning purely because the Worker had last started at 21:27 the night before.</para>
///
/// <para><b>Anchoring is not "fire a timer at 06:00 and hope we're running".</b> The job ticks often
/// and each tick asks whether the most recent due slot has already run, by looking at
/// JobRunHistory. A Worker that boots at 08:47 sees the 06:00 slot was missed and runs it
/// immediately; later ticks that day find it done and skip. Restarts and outages self-heal, and the
/// schedule never drifts.</para>
///
/// <para>Lives in Core, not the Worker, so it is reachable from the test project: this arithmetic
/// crosses DST twice a year and had <b>no tests at all</b> while it sat as an internal helper inside
/// UlsWatcherJob, purely because the tests could not see it.</para>
///
/// <para>Extracted from UlsWatcherJob (2026-08-06) when LicenseWatchJob needed the same behaviour —
/// the alternative was a second copy of DST-sensitive date arithmetic that had no tests at all.</para>
/// </summary>
public static class DailySlotSchedule
{
    /// <summary>
    /// The most recent scheduled slot (UTC) that is due as of <paramref name="nowEt"/> — the latest
    /// hour matching <c>(hour - startHourEt) % intervalHours == 0</c> that isn't in the future.
    ///
    /// <para>Rolls back across a calendar-day boundary when needed rather than reporting nothing due:
    /// at 03:00 ET on an 08:00/20:00 schedule the due slot is <i>yesterday's</i> 20:00 one.</para>
    ///
    /// <para><b>The Eastern conversion is the point, not decoration.</b> Anything from ~8pm ET onward
    /// is already tomorrow in raw UTC, so computing "what hour is it" in UTC would silently shift
    /// every evening slot to the wrong day. Same rule the rest of this codebase follows for day
    /// arithmetic.</para>
    /// </summary>
    public static DateTime LatestDueSlotUtc(DateTime nowEt, int startHourEt, int intervalHours)
    {
        // A zero interval is a DivideByZero two lines down, and the stack trace names neither the job
        // nor the setting. A default-constructed SystemSettings row has exactly that, and the admin
        // form's min="1" is client-side only. Callers should coerce via JobSchedules.IntervalOrDefault;
        // this is the backstop that says what went wrong.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalHours);

        var hoursSinceStart = ((nowEt.Hour - startHourEt) % intervalHours + intervalHours) % intervalHours;
        var slotEt = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, nowEt.Hour, 0, 0, DateTimeKind.Unspecified)
            .AddHours(-hoursSinceStart);
        return TimeZoneInfo.ConvertTimeToUtc(slotEt, UlsSchedule.EasternTimeZone);
    }

    /// <summary>
    /// The next slot (UTC) strictly after <paramref name="nowEt"/> — what the admin Job Schedule page
    /// reports as "next run" once the current slot has already run.
    ///
    /// <para><b>Advances in Eastern wall-clock, not by adding hours to the UTC result.</b> Adding
    /// <paramref name="intervalHours"/> to the previous slot's UTC value lands an hour off across a
    /// DST boundary — 06:00 ET is 10:00 UTC in summer and 11:00 UTC in winter — so the answer would
    /// be quietly wrong twice a year, in the direction nobody checks. Converting the *local* hour
    /// after stepping keeps it pinned to the stated wall-clock time.</para>
    /// </summary>
    public static DateTime NextSlotUtc(DateTime nowEt, int startHourEt, int intervalHours)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalHours);

        var hoursSinceStart = ((nowEt.Hour - startHourEt) % intervalHours + intervalHours) % intervalHours;
        var nextSlotEt = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, nowEt.Hour, 0, 0, DateTimeKind.Unspecified)
            .AddHours(-hoursSinceStart)
            .AddHours(intervalHours);
        return TimeZoneInfo.ConvertTimeToUtc(nextSlotEt, UlsSchedule.EasternTimeZone);
    }

    /// <summary>Current Eastern wall-clock time, for feeding <see cref="LatestDueSlotUtc"/>.</summary>
    public static DateTime NowEastern(TimeProvider timeProvider) =>
        TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, UlsSchedule.EasternTimeZone);
}
