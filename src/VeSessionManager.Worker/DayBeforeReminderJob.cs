using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
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
/// than a duplicate send. Looped per Team — each team has its own SMTP account (multi-team, see
/// docs/multi-team.md).
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
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var notificationService = scope.ServiceProvider.GetRequiredService<CandidateNotificationService>();

            var teams = await dbContext.Teams.ToListAsync(stoppingToken);
            foreach (var team in teams)
            {
                await jobRunHistoryLogger.RunAsync(
                    "DayBeforeReminder",
                    ct => notificationService.SendDayBeforeRemindersAsync(team, ct),
                    team.Id,
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
