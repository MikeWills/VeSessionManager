using System.Text;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Builds a minimal, RFC 5545-valid .ics calendar invite for one session (#491). Deliberately a
/// standalone, pure builder — nothing here reads a Team/Candidate/EmailSettings, and nothing here
/// decides whether to attach one. That decision (a per-team "include a calendar invite" toggle, and
/// which trigger points are genuinely "about one upcoming session" rather than a batch/digest) is
/// separate follow-up work: today no <c>IMessageTriggerScanner</c> actually populates
/// <c>MessageSubject.Session</c> (it exists on the record but nothing constructs one yet), so wiring
/// this into the send path means teaching the relevant scanners to populate it first. This builder is
/// the piece that doesn't depend on any of those decisions being made yet.
/// </summary>
public static class IcsInviteBuilder
{
    /// <param name="uid">
    /// A stable, globally-unique id for this event — reusing the same id on a later send (e.g. the
    /// registration confirmation and a reminder for the same session/candidate) lets a calendar
    /// client update the existing entry instead of creating a duplicate. Callers should derive this
    /// from something that doesn't change for the same session (e.g. the session's own id), not
    /// generate a fresh Guid per send.
    /// </param>
    /// <param name="title">The event's summary — typically the session's own title.</param>
    /// <param name="startUtc">Must actually be UTC (<see cref="DateTimeKind.Utc"/>) — this never reads or trusts Kind, only the instant, so passing a local/unspecified DateTime here silently produces a wrong time.</param>
    /// <param name="durationMinutes">Session.DurationMinutes — must be positive; DTEND is computed as start + this.</param>
    /// <param name="location">The Zoom join URL for a virtual session, or a physical address — rendered as both LOCATION and, when it looks like a URL, a clickable DESCRIPTION line (some clients render LOCATION as plain text only).</param>
    public static string Build(string uid, string title, DateTime startUtc, int durationMinutes, string? location)
    {
        var endUtc = startUtc.AddMinutes(durationMinutes);
        var stampUtc = DateTime.UtcNow;

        var sb = new StringBuilder();
        // CRLF line endings are mandatory per RFC 5545 §3.1, not a Windows-vs-Unix stylistic choice —
        // several real calendar clients (Outlook chief among them) reject or mis-parse a file using
        // bare \n.
        void Line(string text) => sb.Append(text).Append("\r\n");

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//VE Ops//Session Invite//EN");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("BEGIN:VEVENT");
        Line($"UID:{Escape(uid)}");
        Line($"DTSTAMP:{FormatUtc(stampUtc)}");
        Line($"DTSTART:{FormatUtc(startUtc)}");
        Line($"DTEND:{FormatUtc(endUtc)}");
        Line($"SUMMARY:{Escape(title)}");
        if (!string.IsNullOrWhiteSpace(location))
        {
            Line($"LOCATION:{Escape(location)}");
            Line($"DESCRIPTION:{Escape(location)}");
        }
        Line("END:VEVENT");
        Line("END:VCALENDAR");

        return sb.ToString();
    }

    private static string FormatUtc(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>
    /// RFC 5545 §3.3.11 TEXT escaping — a session title or Zoom URL is free-form data, not markup
    /// this format controls, so any of these four characters appearing in it would otherwise corrupt
    /// the file structure rather than just looking odd.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n");
}
