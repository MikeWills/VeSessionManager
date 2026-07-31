using System.Globalization;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Web;

/// <summary>
/// The one place every UI page converts a stored UTC instant to US Eastern for display, always
/// suffixed "ET" so it's never mistaken for UTC or the viewer's own local time. Reuses
/// UlsSchedule.EasternTimeZone (already resolved via the cross-platform "America/New_York" IANA
/// id, verified on both Windows and the Linux deploy target) rather than re-resolving a second
/// TimeZoneInfo for the same zone. EF Core/Sqlite round-trips DateTime as Kind=Unspecified, which
/// TimeZoneInfo.ConvertTimeFromUtc requires Kind=Utc for — same "force Kind=Utc first" gotcha
/// DiscordEventClient.ToOffset() already works around elsewhere in this app.
///
/// Only for genuine instants (an event that happened at a specific moment) — a UTC-midnight-stamped
/// calendar date with no real time component (e.g. FeeConfiguration.EffectiveDate) should NOT be run
/// through this, since shifting it to Eastern would push it back to the previous day's evening and
/// change the displayed date, not just its label.
/// </summary>
public static class EasternTimeFormatter
{
    public static string Format(DateTime utc, string format) =>
        ToEastern(utc).ToString(format, CultureInfo.InvariantCulture) + " ET";

    public static string? Format(DateTime? utc, string format) =>
        utc is null ? null : Format(utc.Value, format);

    private static DateTime ToEastern(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), UlsSchedule.EasternTimeZone);
}
