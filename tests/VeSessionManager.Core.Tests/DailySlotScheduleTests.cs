using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Wall-clock Eastern slot arithmetic, shared by UlsWatcherJob (08:00/20:00 ET by default) and
/// LicenseWatchJob (06:00 ET daily).
///
/// <para>This had <b>no tests at all</b> until 2026-08-06 — not through neglect, but because it was
/// an internal helper inside the Worker, which the test project doesn't reference. Moving it to Core
/// is what made it testable, and the DST cases below are why that mattered.</para>
/// </summary>
public class DailySlotScheduleTests
{
    private static readonly TimeZoneInfo Eastern = UlsSchedule.EasternTimeZone;

    private static DateTime Et(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    private static DateTime ExpectedUtc(DateTime slotEt) => TimeZoneInfo.ConvertTimeToUtc(slotEt, Eastern);

    // ---- Daily schedule (LicenseWatchJob: 06:00 ET, every 24h) -----------------------------------

    [Theory]
    [InlineData(6, 0)]    // exactly on the slot
    [InlineData(9, 30)]   // later the same morning
    [InlineData(23, 59)]  // last minute of the day
    public void DailySlot_AfterTheHour_IsTodaysSlot(int hour, int minute)
    {
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 8, 6, hour, minute), startHourEt: 6, intervalHours: 24);

        Assert.Equal(ExpectedUtc(Et(2026, 8, 6, 6)), due);
    }

    /// <summary>
    /// Before the hour, the due slot is *yesterday's* — not "nothing due". A job that returned no due
    /// slot overnight would skip its catch-up entirely after a restart at 03:00.
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 59)]
    public void DailySlot_BeforeTheHour_RollsBackToYesterday(int hour, int minute)
    {
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 8, 6, hour, minute), startHourEt: 6, intervalHours: 24);

        Assert.Equal(ExpectedUtc(Et(2026, 8, 5, 6)), due);
    }

    // ---- Twice-daily schedule (UlsWatcherJob: 08:00/20:00 ET) ------------------------------------

    [Theory]
    [InlineData(8, 2026, 8, 6, 8)]    // on the morning slot
    [InlineData(19, 2026, 8, 6, 8)]   // still the morning slot
    [InlineData(20, 2026, 8, 6, 20)]  // evening slot
    [InlineData(23, 2026, 8, 6, 20)]  // still the evening slot
    [InlineData(3, 2026, 8, 5, 20)]   // small hours -> yesterday evening
    public void TwiceDaily_PicksTheMostRecentSlot(int nowHourEt, int y, int m, int d, int expectedHourEt)
    {
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 8, 6, nowHourEt), startHourEt: 8, intervalHours: 12);

        Assert.Equal(ExpectedUtc(Et(y, m, d, expectedHourEt)), due);
    }

    // ---- The reason this belongs in Core: DST ----------------------------------------------------

    /// <summary>
    /// 06:00 ET is 10:00 UTC in summer and 11:00 UTC in winter. Computing the slot in UTC — or
    /// assuming a fixed offset — silently shifts every run by an hour twice a year, and the failure
    /// is invisible: the job still runs, just not when anyone thinks.
    /// </summary>
    [Fact]
    public void SameWallClockHour_MapsToDifferentUtc_AcrossTheDstBoundary()
    {
        var summer = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 7, 15, 7), startHourEt: 6, intervalHours: 24);
        var winter = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 12, 15, 7), startHourEt: 6, intervalHours: 24);

        Assert.Equal(10, summer.Hour); // EDT, UTC-4
        Assert.Equal(11, winter.Hour); // EST, UTC-5
        Assert.Equal(DateTimeKind.Utc, summer.Kind);
    }

    /// <summary>The day after the autumn change still resolves to 06:00 local, not 05:00 or 07:00.</summary>
    [Fact]
    public void TheDayAfterFallBack_StillAnchorsToLocalSixAm()
    {
        // US DST ends 2026-11-01; the 2nd is the first full EST day.
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 11, 2, 9), startHourEt: 6, intervalHours: 24);

        Assert.Equal(ExpectedUtc(Et(2026, 11, 2, 6)), due);
        Assert.Equal(6, TimeZoneInfo.ConvertTimeFromUtc(due, Eastern).Hour);
    }

    [Fact]
    public void TheDayAfterSpringForward_StillAnchorsToLocalSixAm()
    {
        // US DST begins 2026-03-08; the 9th is the first full EDT day.
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 3, 9, 9), startHourEt: 6, intervalHours: 24);

        Assert.Equal(ExpectedUtc(Et(2026, 3, 9, 6)), due);
        Assert.Equal(6, TimeZoneInfo.ConvertTimeFromUtc(due, Eastern).Hour);
    }

    // ---- The hour that does not exist (issue #315) ----------------------------------------------

    /// <summary>
    /// On the spring-forward day the Eastern clock jumps 01:59:59 -> 03:00:00, so <b>02:00 ET does
    /// not occur at all</b> — and <c>TimeZoneInfo.ConvertTimeToUtc</c> throws
    /// <c>ArgumentException</c> for a local time it believes is invalid.
    ///
    /// <para>02:00 ET is a defensible setting for these jobs, not a contrived one: it is exactly when
    /// FCC posts its nightly changes, which is the event the whole anchor exists to follow.</para>
    ///
    /// <para>In the Worker a throw here is caught by <c>JobTick.GuardedAsync</c> — the tick is
    /// abandoned and retried an hour later, which is survivable. <c>JobScheduleService</c> has no
    /// such guard, so the admin Job Schedule page would simply 500 for the day.</para>
    ///
    /// <para>The slot is rolled <i>forward</i> out of the gap rather than back: the job should run at
    /// the first instant that exists at or after its nominal time, not an hour early.</para>
    /// </summary>
    [Fact]
    public void ASlotHourInsideTheSpringForwardGap_RollsForwardInsteadOfThrowing()
    {
        // US DST begins 2026-03-08 at 02:00 ET. Asking at 05:00 ET on a 02:00/24h schedule puts the
        // due slot squarely in the missing hour.
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 3, 8, 5), startHourEt: 2, intervalHours: 24);

        // 03:00 EDT is 07:00 UTC — the first moment that actually exists at or after 02:00 that day.
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), due);
        Assert.Equal(DateTimeKind.Utc, due.Kind);
    }

    [Fact]
    public void ANextSlotInsideTheSpringForwardGap_RollsForwardInsteadOfThrowing()
    {
        // 23:00 ET on the 7th, 02:00/24h: the next slot is 02:00 on the 8th, which does not exist.
        var next = DailySlotSchedule.NextSlotUtc(Et(2026, 3, 7, 23), startHourEt: 2, intervalHours: 24);

        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>
    /// The autumn counterpart, for completeness: 01:30 ET occurs *twice* on the fall-back day.
    /// <c>ConvertTimeToUtc</c> does not throw for an ambiguous time — it resolves to standard time —
    /// so this needs no special handling, only a test saying so, since "the other DST edge" is the
    /// first thing a reader will wonder about.
    /// </summary>
    [Fact]
    public void AnAmbiguousFallBackHour_ResolvesWithoutThrowing()
    {
        // US DST ends 2026-11-01 at 02:00 ET; 01:00 occurs twice.
        var due = DailySlotSchedule.LatestDueSlotUtc(Et(2026, 11, 1, 4), startHourEt: 1, intervalHours: 24);

        Assert.Equal(DateTimeKind.Utc, due.Kind);
        // EST (UTC-5) is what ConvertTimeToUtc picks for an ambiguous local time.
        Assert.Equal(new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), due);
    }

    /// <summary>
    /// The evening case that motivates working in Eastern at all: at 21:00 ET the UTC date has
    /// already rolled to tomorrow, so any "what hour is it" done in UTC lands on the wrong day.
    /// </summary>
    [Fact]
    public void LateEveningEastern_ResolvesAgainstTheEasternDate_NotTheUtcOne()
    {
        var nowEt = Et(2026, 8, 6, 21);
        Assert.Equal(7, TimeZoneInfo.ConvertTimeToUtc(nowEt, Eastern).Day); // UTC has already rolled over

        var due = DailySlotSchedule.LatestDueSlotUtc(nowEt, startHourEt: 6, intervalHours: 24);

        Assert.Equal(ExpectedUtc(Et(2026, 8, 6, 6)), due); // still the 6th's slot
    }
}
