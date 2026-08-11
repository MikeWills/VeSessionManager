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
        return ToUtc(slotEt);
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
        return ToUtc(nextSlotEt);
    }

    /// <summary>
    /// Eastern wall-clock to UTC, rolling forward out of the spring-forward gap (issue #315).
    ///
    /// <para>On the day DST begins the Eastern clock jumps 01:59:59 → 03:00:00, so <b>02:00 ET does
    /// not occur</b> and <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> throws
    /// <see cref="ArgumentException"/> for it. 02:00 is a defensible setting for these jobs rather
    /// than a contrived one — it is exactly when FCC posts its nightly changes, the event this whole
    /// anchor exists to follow — so the throw was reachable by configuration alone, once a year.</para>
    ///
    /// <para>In the Worker <c>JobTick.GuardedAsync</c> would have caught it and retried an hour
    /// later, which is survivable. <c>JobScheduleService</c> has no such guard, so the admin Job
    /// Schedule page would have simply 500'd for the day.</para>
    ///
    /// <para><b>Forward, not back:</b> the job should run at the first instant that exists at or
    /// after its nominal time, never an hour early. A loop rather than a single <c>AddHours(1)</c>
    /// because the one-hour US gap is a fact about this zone today, not about time zones — and this
    /// arithmetic already exists to stop exactly that kind of assumption being baked in.</para>
    ///
    /// <para>The autumn counterpart needs nothing: an <i>ambiguous</i> local time (01:30 occurring
    /// twice) does not throw — <c>ConvertTimeToUtc</c> resolves it to standard time — which
    /// <c>AnAmbiguousFallBackHour_ResolvesWithoutThrowing</c> pins so the asymmetry is not mistaken
    /// for an oversight.</para>
    /// </summary>
    private static DateTime ToUtc(DateTime slotEt)
    {
        while (UlsSchedule.EasternTimeZone.IsInvalidTime(slotEt))
        {
            slotEt = slotEt.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(slotEt, UlsSchedule.EasternTimeZone);
    }

    /// <summary>Current Eastern wall-clock time, for feeding <see cref="LatestDueSlotUtc"/>.</summary>
    public static DateTime NowEastern(TimeProvider timeProvider) =>
        TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, UlsSchedule.EasternTimeZone);
}
