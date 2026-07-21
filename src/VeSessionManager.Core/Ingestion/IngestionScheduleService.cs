using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Decides whether SessionIngestionJob's per-team pipeline (ingestion, VE roster sync, Zoom/Discord
/// scheduling, Square payment links, confirmation emails — the whole thing runs or skips together,
/// see CLAUDE.md) is due to run for a given Team on the current tick.
///
/// Most teams run a session once a day or less, so polling ExamTools every 5 minutes around the
/// clock has no real upside almost all the time — SystemSettings.SessionIngestionIntervalMinutes
/// (default 60) is the "normal" cadence for a team with nothing imminent. But a team "surges" back
/// to the job's own tick cadence (effectively every tick) whenever it has an Active session either
/// starting within the next 60 minutes or still within its own Duration — covering both "registered
/// minutes before start" and "registered just after start" — so a last-minute registrant is still
/// picked up quickly without needing every team polled aggressively all day, every day.
///
/// Team.LastIngestionRunUtc is internal bookkeeping only (no admin UI) — null means "never run for
/// this team yet," which is always due.
/// </summary>
public class IngestionScheduleService(AppDbContext dbContext, TimeProvider timeProvider)
{
    private const int SurgeWindowBeforeStartMinutes = 60;

    public async Task<bool> IsDueAsync(Team team, int normalIntervalMinutes, CancellationToken cancellationToken)
    {
        if (team.LastIngestionRunUtc is null)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var isSurging = await dbContext.Sessions.AnyAsync(s =>
            s.TeamId == team.Id
            && s.Status == SessionStatus.Active
            && s.TestingCompletedUtc == null
            && now >= s.ScheduledStartUtc.AddMinutes(-SurgeWindowBeforeStartMinutes)
            && now <= s.ScheduledStartUtc.AddMinutes(s.DurationMinutes),
            cancellationToken);

        // Surging means "due on effectively every tick" — the tick cadence itself
        // (Jobs:SessionIngestionIntervalSeconds) is the surge interval, so any elapsed time at all
        // since the last run is enough.
        var requiredInterval = isSurging ? TimeSpan.Zero : TimeSpan.FromMinutes(normalIntervalMinutes);

        return now - team.LastIngestionRunUtc.Value >= requiredInterval;
    }
}
