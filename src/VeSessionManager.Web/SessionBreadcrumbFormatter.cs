namespace VeSessionManager.Web;

/// <summary>
/// Formats a session for display as "(ExtId) Title" — e.g. "(KM6Z - W5CBW) Summer POTA time!" —
/// shared by the session list, Detail, and CandidateDetail pages. Session.ExtId is ExamTools' own
/// short lead-VE-callsign code, the same parenthetical text ExamTools' own calendar UI shows next
/// to the team name; requested 2026-07-30 after Session.ExamToolsSessionId (a raw Mongo id) turned
/// out to be meaningless to a user for exactly this purpose. Falls back to just the title when
/// ExtId is null — sessions ingested before this field existed.
/// </summary>
public static class SessionBreadcrumbFormatter
{
    public static string Format(string? extId, string title) =>
        string.IsNullOrWhiteSpace(extId) ? title : $"({extId}) {title}";
}
