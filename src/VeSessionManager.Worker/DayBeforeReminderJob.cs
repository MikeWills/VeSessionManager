using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 4's "daily job" (per the spec, distinct from the ~5-minute ingestion/scheduling/payment
/// tick) that finds candidates whose session is tomorrow and sends the reminder. Runs on a
/// 24-hour PeriodicTimer starting from whenever the Worker process starts, not pinned to a
/// specific wall-clock time of day — simplest option consistent with every other job in this
/// codebase (PeriodicTimer, no cron/Quartz dependency); acceptable since
/// CandidateNotificationService's send-once tracking makes an extra same-day tick a no-op rather
/// than a duplicate send.
/// </summary>
public class DayBeforeReminderJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("Jobs:DayBeforeReminderIntervalHours", 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var notificationService = scope.ServiceProvider.GetRequiredService<CandidateNotificationService>();

            await jobRunHistoryLogger.RunAsync(
                "DayBeforeReminder",
                notificationService.SendDayBeforeRemindersAsync,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
