using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// User-triggered entry points into TeamPipeline, which is the single definition of the step order
/// (previously written out here twice and again in SessionIngestionJob — see that class for what
/// the duplication cost). Equivalent of SessionIngestionJob's per-team pipeline (ingestion, VE roster sync,
/// exam result sync, Zoom/Discord scheduling, Square payment links, confirmation emails, same order, same reasoning —
/// see the Worker job's own doc comment), instead of waiting for SessionIngestionJob's own tick.
/// Added when the job's imminent-session "surge" polling was removed (see
/// CLAUDE.md/IngestionScheduleService) — a Session Manager who sees a new registrant in ExamTools
/// can pull them in immediately rather than waiting up to
/// SystemSettings.SessionIngestionIntervalMinutes for the next scheduled poll.
///
/// Two entry points, two scopes (split 2026-08-03):
///  - RunAsync (whole team) — Admin → Team Maintenance's "Refresh now" button. The full-feed diff,
///    including session create/cancel detection, which is inherently team-wide (a session id
///    disappearing from the feed IS the cancellation signal — a partial feed would look like mass
///    cancellation).
///  - RunForSessionAsync (one session) — the session Detail page's "Refresh candidates" button.
///    Previously that button ran the team-wide pipeline too, which meant clicking it on one session
///    could generate payment links and send confirmation emails for every OTHER session the team
///    had — far more side effects than the button implied. Now it re-syncs only that session's
///    candidates/roster/results and runs scheduling, payment links and confirmation emails
///    restricted to that session; the rest of the team catches up on the Worker's next tick.
///
/// Job names are prefixed "Manual" so JobRunHistory's ops dashboard can tell a user-triggered run
/// apart from the background job's own ticks at a glance; both scopes share the same names, since
/// the dashboard distinction that matters is manual-vs-scheduled, not which button.
/// </summary>
public class ManualCandidateRefreshService(TeamPipeline pipeline)
{
    /// <summary>Whole-team refresh — Admin → Team Maintenance's "Refresh now".</summary>
    public async Task<ManualRefreshResult> RunAsync(Team team, CancellationToken cancellationToken) =>
        ToResult(await pipeline.RunAsync(team, ManualJobNamePrefix, onlySessionId: null, cancellationToken));

    /// <summary>Session-scoped variant — see the class doc comment for the split's rationale.</summary>
    public async Task<ManualRefreshResult> RunForSessionAsync(Team team, int sessionId, CancellationToken cancellationToken) =>
        ToResult(await pipeline.RunAsync(team, ManualJobNamePrefix, sessionId, cancellationToken));

    /// <summary>Prefixed so the ops dashboard can tell a user-triggered run from the Worker's own tick at a glance.</summary>
    private const string ManualJobNamePrefix = "Manual";

    private static ManualRefreshResult ToResult(TeamPipelineResult result) =>
        new(result.Ingestion.CandidatesAdded, result.Ingestion.CandidatesUpdated, result.Email.Sent, result.FailedSteps);
}

/// <param name="FailedSteps">
/// Pipeline steps that threw (#242). <b>Check this before reporting the counts.</b> A total failure
/// produces (0, 0, 0) exactly like a run with nothing to do, so the counts alone cannot tell a
/// caller which happened — and both page handlers used to render the same green sentence for both.
/// </param>
public record ManualRefreshResult(int CandidatesAdded, int CandidatesUpdated, int ConfirmationEmailsSent, int FailedSteps)
{
    /// <summary>
    /// The sentence to show the user, and whether it is good news. One definition, because the two
    /// call sites differ only in whether they name the team — and they previously differed in being
    /// wrong in the same way twice.
    /// </summary>
    public (bool Success, string Message) Describe(string? teamName)
    {
        var subject = teamName is null ? "Refreshed" : $"Refreshed {teamName}";

        if (FailedSteps > 0)
        {
            // Deliberately does not restate the zero counts: they are meaningless here, and printing
            // "0 new candidate(s)" beside a failure is what made the original message so convincing.
            // Job History is named because it holds the actual error for each failed step.
            return (false,
                $"{subject} — {FailedSteps} step(s) failed. Nothing may have been picked up. " +
                "See Admin → Job History for the error.");
        }

        return (true,
            $"{subject} — {CandidatesAdded} new candidate(s), {CandidatesUpdated} updated, " +
            $"{ConfirmationEmailsSent} confirmation email(s) sent.");
    }
}
