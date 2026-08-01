using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Issue #77: debounce for the **team-level** "Refresh now" action on Admin → Team Maintenance.
/// A team-level refresh runs the team's entire pipeline against ExamTools' API on demand, so a
/// double-click — or an impatient admin watching for a registrant to appear — should not stack
/// concurrent full passes over someone else's servers.
///
/// The per-session "Refresh candidates" button on Pages/SessionManager/Detail.cshtml is
/// deliberately **not** throttled and does not use this: it is pressed by a Session Manager working
/// one session in real time, which is exactly the situation the throttle would get in the way of.
///
/// Schema-free on purpose — it reads the JobRunHistory rows ManualCandidateRefreshService already
/// writes ("ManualSessionIngestion", the first step of every manual run) rather than adding a
/// Team column and a migration for a 60-second value. That also means the throttle is naturally
/// shared across web instances and survives a restart, since the evidence lives in the database
/// rather than in process memory.
/// </summary>
public class TeamRefreshThrottle(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Job name written by ManualCandidateRefreshService's first step — the marker this reads.</summary>
    private const string ManualIngestionJobName = "ManualSessionIngestion";

    /// <summary>
    /// Long enough to swallow a double-click and a reflexive second press, short enough that a
    /// genuine "it didn't pick up my change, try again" retry is never meaningfully blocked.
    /// </summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns null when a refresh may proceed, or the number of seconds still to wait when one ran
    /// too recently for this team.
    /// </summary>
    public async Task<int?> SecondsUntilAllowedAsync(int teamId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lastStartedUtc = await dbContext.JobRunHistories
            .Where(j => j.TeamId == teamId && j.JobName == ManualIngestionJobName)
            .MaxAsync(j => (DateTime?)j.StartedUtc, cancellationToken);

        if (lastStartedUtc is null)
        {
            return null;
        }

        var elapsed = now - lastStartedUtc.Value;
        if (elapsed >= Debounce)
        {
            return null;
        }

        // Ceiling, so "0 seconds to wait" is never reported while the refresh is still blocked.
        return (int)Math.Ceiling((Debounce - elapsed).TotalSeconds);
    }
}
