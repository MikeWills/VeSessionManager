using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// The one definition of a team's refresh pipeline: ingest, sync the VE roster, sync exam results,
/// schedule Zoom/Discord, generate payment links, send registration confirmations — in that order.
///
/// <para><b>Why this exists.</b> That ordered list used to be written out three times: in the
/// Worker's SessionIngestionJob, and twice in ManualCandidateRefreshService (team-wide and
/// session-scoped). The steps themselves were never duplicated — all three called the same services
/// — but the *order and membership* were, and they drifted: exam-result sync was missing from the
/// manual path for weeks, despite that class's own doc comment claiming to mirror the job's
/// pipeline. A step added to one copy silently didn't exist in the others, and nothing failed.</para>
///
/// <para>Adding a step now means adding it here, once, and every caller gets it.</para>
/// </summary>
public class TeamPipeline(
    SessionIngestionService ingestionService,
    VolunteerExaminerSyncService veRosterSyncService,
    ExamResultSyncService examResultSyncService,
    SessionEventSchedulingService schedulingService,
    PaymentGenerationService paymentGenerationService,
    CandidateNotificationService notificationService,
    JobRunHistoryLogger jobRunHistoryLogger)
{
    /// <summary>
    /// Runs the pipeline for a team.
    /// </summary>
    /// <param name="jobNamePrefix">
    /// Prepended to every step's JobRunHistory name — <c>""</c> for the Worker's scheduled tick,
    /// <c>"Manual"</c> for a user-triggered refresh. The ops dashboard's useful distinction is
    /// manual-vs-scheduled, not which button was pressed, so both manual scopes share it.
    /// </param>
    /// <param name="onlySessionId">
    /// Null runs the whole team. A session id restricts every step to that session — the Detail
    /// page's "Refresh candidates", which must not mint payment links or email candidates for every
    /// *other* session the team has.
    /// <para><b>Two steps change method rather than take a filter</b>, which is why this is a
    /// branch and not just an argument passed through:</para>
    /// <list type="bullet">
    /// <item>Ingestion uses RefreshSessionCandidatesAsync, because the team-wide RunAsync cancels
    /// sessions missing from the feed and a single-session view looks like mass cancellation.</item>
    /// <item>Exam results use SyncSessionAsync, which deliberately ignores ResultSyncWindow — the
    /// Detail page's refresh is the documented on-demand path for a session graded later than the
    /// window.</item>
    /// </list>
    /// </param>
    public async Task<TeamPipelineResult> RunAsync(Team team, string jobNamePrefix, int? onlySessionId, CancellationToken cancellationToken)
    {
        var ingestion = new IngestionResult();
        await RunStepAsync("SessionIngestion", async ct => ingestion = onlySessionId is { } ingestSessionId
            ? await ingestionService.RefreshSessionCandidatesAsync(team, ingestSessionId, ct)
            : await ingestionService.RunAsync(team, ct));

        await RunStepAsync("VeRosterSync", ct => veRosterSyncService.RunAsync(team, ct, onlySessionId));

        await RunStepAsync("ExamResultSync", ct => onlySessionId is { } resultSessionId
            ? examResultSyncService.SyncSessionAsync(team, resultSessionId, ct)
            : examResultSyncService.RunAsync(team, ct));

        await RunStepAsync("SessionEventScheduling", ct => schedulingService.RunAsync(team, ct, onlySessionId));

        await RunStepAsync("PaymentGeneration", ct => paymentGenerationService.RunAsync(team, ct, onlySessionId));

        var email = new EmailNotificationResult();
        await RunStepAsync("RegistrationConfirmation", async ct =>
            email = await notificationService.SendRegistrationConfirmationsAsync(team, ct, onlySessionId));

        return new TeamPipelineResult(ingestion, email);

        // Each step is passed as a result-returning delegate on purpose: that binds
        // JobRunHistoryLogger's generic overload, which records the step's own summary
        // ("sent 0, failed 1") on the history row. A void-returning step would log nothing useful.
        Task RunStepAsync<TResult>(string name, Func<CancellationToken, Task<TResult>> step) =>
            jobRunHistoryLogger.RunAsync(jobNamePrefix + name, step, team.Id, cancellationToken);
    }
}

/// <summary>What the pipeline produced. Only the two results any caller currently reports on — the others are recorded on their JobRunHistory rows.</summary>
public record TeamPipelineResult(IngestionResult Ingestion, EmailNotificationResult Email);
