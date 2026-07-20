using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;

namespace VeSessionManager.Worker;

/// <summary>
/// Polls ExamTools on a timer, runs the Session/Candidate ingestion diff (Phase 1), then —
/// per the spec's "hook this into the end of Phase 1's new session detected path" — runs Phase
/// 2's Zoom/Discord scheduling pass, Phase 3's Square payment-link generation, and Phase 4's
/// registration-confirmation email in the same tick, in that order: by the time confirmations go
/// out, the session's Zoom link and (if the VEC collects a fee) the candidate's payment link have
/// had their best chance to already exist, so the email reads as complete rather than partial.
/// Each step still gets its own JobRunHistory entry: a failure in one step shouldn't read as a
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
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var ingestionService = scope.ServiceProvider.GetRequiredService<SessionIngestionService>();
            var schedulingService = scope.ServiceProvider.GetRequiredService<SessionEventSchedulingService>();
            var paymentGenerationService = scope.ServiceProvider.GetRequiredService<PaymentGenerationService>();
            var notificationService = scope.ServiceProvider.GetRequiredService<CandidateNotificationService>();

            await jobRunHistoryLogger.RunAsync(
                "SessionIngestion",
                ingestionService.RunAsync,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "SessionEventScheduling",
                schedulingService.RunAsync,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "PaymentGeneration",
                paymentGenerationService.RunAsync,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "RegistrationConfirmation",
                notificationService.SendRegistrationConfirmationsAsync,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
