using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// User-triggered equivalent of SessionIngestionJob's per-team pipeline (ingestion, VE roster sync,
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
public class ManualCandidateRefreshService(
    SessionIngestionService ingestionService,
    VolunteerExaminerSyncService veRosterSyncService,
    ExamResultSyncService examResultSyncService,
    SessionEventSchedulingService schedulingService,
    PaymentGenerationService paymentGenerationService,
    CandidateNotificationService notificationService,
    JobRunHistoryLogger jobRunHistoryLogger)
{
    public async Task<ManualRefreshResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var ingestionResult = new IngestionResult();
        await jobRunHistoryLogger.RunAsync(
            "ManualSessionIngestion",
            async ct => ingestionResult = await ingestionService.RunAsync(team, ct),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualVeRosterSync",
            ct => veRosterSyncService.RunAsync(team, ct),
            team.Id,
            cancellationToken);

        // Was missing entirely until issue #81, despite this class's own doc comment claiming to
        // mirror SessionIngestionJob's pipeline — which has run this step since 2026-07-28. Now
        // load-bearing rather than merely consistent: ExamResultSyncService stopped scanning
        // sessions older than its window, so this is the only way to pull in a session graded later
        // than that.
        await jobRunHistoryLogger.RunAsync(
            "ManualExamResultSync",
            ct => examResultSyncService.RunAsync(team, ct),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualSessionEventScheduling",
            ct => schedulingService.RunAsync(team, ct),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualPaymentGeneration",
            ct => paymentGenerationService.RunAsync(team, ct),
            team.Id,
            cancellationToken);

        var emailResult = new EmailNotificationResult();
        await jobRunHistoryLogger.RunAsync(
            "ManualRegistrationConfirmation",
            async ct => emailResult = await notificationService.SendRegistrationConfirmationsAsync(team, ct),
            team.Id,
            cancellationToken);

        return new ManualRefreshResult(
            ingestionResult.CandidatesAdded,
            ingestionResult.CandidatesUpdated,
            emailResult.Sent);
    }

    /// <summary>Session-scoped variant — see the class doc comment for the split's rationale.</summary>
    public async Task<ManualRefreshResult> RunForSessionAsync(Team team, int sessionId, CancellationToken cancellationToken)
    {
        var ingestionResult = new IngestionResult();
        await jobRunHistoryLogger.RunAsync(
            "ManualSessionIngestion",
            async ct => ingestionResult = await ingestionService.RefreshSessionCandidatesAsync(team, sessionId, ct),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualVeRosterSync",
            ct => veRosterSyncService.RunAsync(team, ct, sessionId),
            team.Id,
            cancellationToken);

        // Unlike the team-wide RunAsync above, this deliberately ignores ResultSyncWindow — the
        // Detail page's refresh is the documented on-demand path for a session graded later than
        // the window (see ExamResultSyncService.SyncSessionAsync).
        await jobRunHistoryLogger.RunAsync(
            "ManualExamResultSync",
            ct => examResultSyncService.SyncSessionAsync(team, sessionId, ct),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualSessionEventScheduling",
            ct => schedulingService.RunAsync(team, ct, sessionId),
            team.Id,
            cancellationToken);

        await jobRunHistoryLogger.RunAsync(
            "ManualPaymentGeneration",
            ct => paymentGenerationService.RunAsync(team, ct, sessionId),
            team.Id,
            cancellationToken);

        var emailResult = new EmailNotificationResult();
        await jobRunHistoryLogger.RunAsync(
            "ManualRegistrationConfirmation",
            async ct => emailResult = await notificationService.SendRegistrationConfirmationsAsync(team, ct, sessionId),
            team.Id,
            cancellationToken);

        return new ManualRefreshResult(
            ingestionResult.CandidatesAdded,
            ingestionResult.CandidatesUpdated,
            emailResult.Sent);
    }
}

public record ManualRefreshResult(int CandidatesAdded, int CandidatesUpdated, int ConfirmationEmailsSent);
