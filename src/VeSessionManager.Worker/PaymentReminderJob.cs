using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 6's daily job: 5-day payment reminders, 10-day expiration notices, and stale-Unmatched
/// review flags. Same 24-hour PeriodicTimer idiom as DayBeforeReminderJob/FccDailyWatcherJob — not
/// pinned to a specific wall-clock time; PaymentReminderService's own tracking fields
/// (PaymentReminderSentUtc/ExpiredUnpaid/UnmatchedReviewFlaggedUtc) make an extra same-day tick a
/// no-op rather than a duplicate send/flag. Looped per Team — each team has its own SMTP account
/// (multi-team, see docs/multi-team.md).
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
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var reminderService = scope.ServiceProvider.GetRequiredService<PaymentReminderService>();

            var teams = await dbContext.Teams.ToListAsync(stoppingToken);
            foreach (var team in teams)
            {
                await jobRunHistoryLogger.RunAsync(
                    "PaymentReminder",
                    ct => reminderService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
