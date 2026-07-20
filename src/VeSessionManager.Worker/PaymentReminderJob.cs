using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 6's daily job: 5-day payment reminders, 10-day expiration notices, and stale-Unmatched
/// review flags. Same 24-hour PeriodicTimer idiom as DayBeforeReminderJob/FccDailyWatcherJob — not
/// pinned to a specific wall-clock time; PaymentReminderService's own tracking fields
/// (PaymentReminderSentUtc/ExpiredUnpaid/UnmatchedReviewFlaggedUtc) make an extra same-day tick a
/// no-op rather than a duplicate send/flag.
/// </summary>
public class PaymentReminderJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("Jobs:PaymentReminderIntervalHours", 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var reminderService = scope.ServiceProvider.GetRequiredService<PaymentReminderService>();

            await jobRunHistoryLogger.RunAsync(
                "PaymentReminder",
                reminderService.RunAsync,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
