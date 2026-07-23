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
    public bool IsDue(Team team, int normalIntervalMinutes, DateTime nowUtc)
    {
        if (team.LastIngestionRunUtc is null)
        {
            return true;
        }

        return nowUtc - team.LastIngestionRunUtc.Value >= TimeSpan.FromMinutes(normalIntervalMinutes);
    }
}
