using System.Globalization;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.Notifications;

/// <summary>
/// How a session's start time is written to a **candidate**, as opposed to how it is shown to a
/// Session Manager on screen.
///
/// <para><b>Why this exists.</b> Candidate email used to render <c>{{SessionDate}}</c> in UTC —
/// "Saturday, August 15, 2026 at 2:00 PM UTC" for a session every screen in the app shows as 10:00 AM
/// ET. The one surface that speaks to a member of the public was the one speaking a timezone almost
/// none of them use, and a candidate who reads that as local time misses the session by hours. It
/// was not a decision: <c>EasternTimeFormatter</c> lives in the Web project, these emails are built
/// in Core, so the shared formatter was simply out of reach.</para>
///
/// <para><b>Why two zones.</b> Sessions are remote, so candidates are not near the team running
/// them. Eastern and Pacific are the outer edges of the contiguous US, and the gap between them is
/// always exactly three hours (both observe DST, and switch on the same dates), so a reader in
/// Central or Mountain can place themselves between the two without being told. One zone would make
/// three quarters of the country do arithmetic against a label they might not notice.</para>
/// </summary>
public static class SessionTimeFormatter
{
    private static readonly TimeZoneInfo PacificTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    /// <summary>
    /// e.g. <c>Saturday, August 15, 2026 at 10:00 AM ET / 7:00 AM PT</c>.
    ///
    /// <para>When the two zones fall on different calendar days — a start before 3:00 AM Eastern,
    /// which no real session uses but which the format must not misreport — the Pacific side carries
    /// its own date rather than silently inheriting Eastern's.</para>
    /// </summary>
    public static string ForCandidate(DateTime scheduledStartUtc)
    {
        var eastern = ToZone(scheduledStartUtc, UlsSchedule.EasternTimeZone);
        var pacific = ToZone(scheduledStartUtc, PacificTimeZone);

        var easternText = eastern.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);

        return eastern.Date == pacific.Date
            ? $"{easternText} ET / {pacific.ToString("h:mm tt", CultureInfo.InvariantCulture)} PT"
            : $"{easternText} ET / {pacific.ToString("dddd, MMMM d 'at' h:mm tt", CultureInfo.InvariantCulture)} PT";
    }

    /// <summary>
    /// EF Core/SQLite round-trips DateTime as <c>Kind=Unspecified</c>, and
    /// <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> rejects anything that is not
    /// <c>Kind=Utc</c> — the same "force the Kind first" trap recorded in CLAUDE.md.
    /// </summary>
    private static DateTime ToZone(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
