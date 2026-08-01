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
/// see the Worker job's own doc comment) — run on demand for one Team from the "Refresh candidates"
/// button on the session detail page (Pages/SessionManager/Detail.cshtml.cs), instead of waiting for
/// SessionIngestionJob's own tick. Added when the job's imminent-session "surge" polling was removed
/// (see CLAUDE.md/IngestionScheduleService) — a Session Manager who sees a new registrant in
/// ExamTools can pull them in immediately rather than waiting up to
/// SystemSettings.SessionIngestionIntervalMinutes for the next scheduled poll.
///
/// Job names are prefixed "Manual" so JobRunHistory's ops dashboard can tell a user-triggered run
/// apart from the background job's own ticks at a glance.
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
}

public record ManualRefreshResult(int CandidatesAdded, int CandidatesUpdated, int ConfirmationEmailsSent);
