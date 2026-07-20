using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Worker;

/// <summary>
/// Polls ExamTools on a timer, runs the Session/Candidate ingestion diff (Phase 1) once per Team
/// (multi-team foundation — see docs/multi-team.md), then Phase 7's VE roster sync (reuses the same
/// ExamTools credentials, no reason to wait for a separate tick), then — per the spec's "hook this
/// into the end of Phase 1's new session detected path" — runs Phase 2's Zoom/Discord scheduling
/// pass, Phase 3's Square payment-link generation, and Phase 4's registration-confirmation email in
/// the same tick, in that order: by the time confirmations go out, the session's Zoom link and (if
/// the VEC collects a fee) the candidate's payment link have had their best chance to already
/// exist, so the email reads as complete rather than partial.
///
/// Every step is looped per Team — each has its own ExamTools/Zoom/Square/SMTP credentials; Discord
/// shares one bot but each team picks its own Guild (see docs/multi-team.md). Each step still gets
/// its own JobRunHistory entry: a failure in one step (or one team's turn) shouldn't read as a
/// failure in another on the ops dashboard, and each later step should still run against whatever
/// the earlier ones already committed even if it itself later fails.
/// </summary>
public class SessionIngestionJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Jobs:SessionIngestionIntervalSeconds", 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var ingestionService = scope.ServiceProvider.GetRequiredService<SessionIngestionService>();
            var veRosterSyncService = scope.ServiceProvider.GetRequiredService<VolunteerExaminerSyncService>();
            var schedulingService = scope.ServiceProvider.GetRequiredService<SessionEventSchedulingService>();
            var paymentGenerationService = scope.ServiceProvider.GetRequiredService<PaymentGenerationService>();
            var notificationService = scope.ServiceProvider.GetRequiredService<CandidateNotificationService>();

            var teams = await dbContext.Teams.ToListAsync(stoppingToken);
            foreach (var team in teams)
            {
                await jobRunHistoryLogger.RunAsync(
                    "SessionIngestion",
                    ct => ingestionService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);

                await jobRunHistoryLogger.RunAsync(
                    "VeRosterSync",
                    ct => veRosterSyncService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);

                await jobRunHistoryLogger.RunAsync(
                    "SessionEventScheduling",
                    ct => schedulingService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);

                await jobRunHistoryLogger.RunAsync(
                    "PaymentGeneration",
                    ct => paymentGenerationService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);

                await jobRunHistoryLogger.RunAsync(
                    "RegistrationConfirmation",
                    ct => notificationService.SendRegistrationConfirmationsAsync(team, ct),
                    team.Id,
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
