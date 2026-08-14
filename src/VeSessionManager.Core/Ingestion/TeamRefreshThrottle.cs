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
/// <para><b>The residual race, and why it stays.</b> That button runs the pipeline in the Web process
/// while the Worker may be running its own tick over the same session's rows — two writers, one
/// SQLite file. It is narrow: the button has been session-scoped since 2026-08-03 (it used to run the
/// team-wide pipeline, so one click could mint payment links and email candidates for every other
/// session), and the duplicate-payment half is closed by T08's unique index. Nothing has ever been
/// observed hitting it.</para>
///
/// <para>The design that removes it is routing refreshes through a Worker-consumed request row, the
/// HistoricalImportRequest pattern. <b>Considered and rejected 2026-08-14:</b> it makes the button
/// asynchronous, so it returns "queued" instead of what happened — undoing #242, which exists
/// precisely so the button tells the truth about failures, and breaking the click-and-see-the-result
/// loop that is its main use during live session work.</para>
///
/// <para>What would change that: a second Web instance (which breaks single-process assumptions
/// elsewhere too), an actual observed collision, or a polling UI becoming worthwhile for other
/// reasons — at which point "queue it and show the outcome" stops costing the feedback loop. This
/// note lives here rather than in an issue because it constrains whoever next touches this button,
/// and an issue for an unobserved risk whose fix has already been argued against is one nobody
/// actions and everybody re-verifies.</para>
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
