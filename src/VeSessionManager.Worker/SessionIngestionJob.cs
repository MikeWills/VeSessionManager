using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;

namespace VeSessionManager.Worker;

/// <summary>
/// Polls ExamTools on a timer, runs the Session/Candidate ingestion diff (Phase 1) once per Team
/// (multi-team foundation — see docs/multi-team.md), then — per the spec's "hook this into the end
/// of Phase 1's new session detected path" — runs Phase 2's Zoom/Discord scheduling pass, Phase 3's
/// Square payment-link generation, and Phase 4's registration-confirmation email in the same tick,
/// in that order: by the time confirmations go out, the session's Zoom link and (if the VEC
/// collects a fee) the candidate's payment link have had their best chance to already exist, so the
/// email reads as complete rather than partial.
///
/// Those three steps are deliberately still global (one call per tick, not looped per team) — they
/// still use a single shared Zoom/Discord/Square/Email account across every team's sessions, until
/// a later fast-follow gives those integrations the same per-team credential treatment ExamTools
/// just got. Each step still gets its own JobRunHistory entry: a failure in one step (or one team's
/// ingestion) shouldn't read as a failure in another on the ops dashboard, and each later step
/// should still run against whatever the earlier ones already committed even if it itself later
/// fails.
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
            }

            // Global steps: still process ALL sessions/candidates regardless of team, until Zoom/
            // Discord/Square/Email get the same per-team treatment as ExamTools in a later
            // fast-follow (see docs/multi-team.md).
            await jobRunHistoryLogger.RunAsync(
                "SessionEventScheduling",
                schedulingService.RunAsync,
                null,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "PaymentGeneration",
                paymentGenerationService.RunAsync,
                null,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "RegistrationConfirmation",
                notificationService.SendRegistrationConfirmationsAsync,
                null,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
