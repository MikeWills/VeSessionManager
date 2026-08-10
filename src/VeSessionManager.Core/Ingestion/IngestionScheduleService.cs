using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Decides whether SessionIngestionJob's per-team pipeline (ingestion, VE roster sync, Zoom/Discord
/// scheduling, Square payment links, confirmation emails — the whole thing runs or skips together,
/// see CLAUDE.md) is due to run for a given Team on the current tick.
///
/// Most teams run a session once a day or less, so polling ExamTools every 5 minutes around the
/// clock has no real upside almost all the time — SystemSettings.SessionIngestionIntervalMinutes
/// (default 60) is the cadence for every team, flat, with no imminent-session "surge" exception (an
/// earlier version of this service did surge back to the job's own tick cadence near a session's
/// start time, but that was removed in favor of a user-triggered "Refresh candidates" button on the
/// session detail page for exactly the situation the surge existed for — a Session Manager who needs
/// a last-minute registrant pulled in right now).
///
/// Team.LastIngestionRunUtc is internal bookkeeping only (no admin UI) — null means "never run for
/// this team yet," which is always due.
/// </summary>
public class IngestionScheduleService
{
    public bool IsDue(Team team, int normalIntervalMinutes, DateTime nowUtc) =>
        IsDue(team.LastIngestionRunUtc, normalIntervalMinutes, nowUtc);

    /// <summary>
    /// Timestamp overload, for callers holding a projection rather than a Team — IngestionStatusService
    /// projects its rows so it never decrypts a credential it does not need. The rule is identical;
    /// this is the whole of it, and the Team overload delegates here.
    /// </summary>
    public bool IsDue(DateTime? lastIngestionRunUtc, int normalIntervalMinutes, DateTime nowUtc)
    {
        if (lastIngestionRunUtc is null)
        {
            return true;
        }

        return nowUtc - lastIngestionRunUtc.Value >= TimeSpan.FromMinutes(normalIntervalMinutes);
    }
}
