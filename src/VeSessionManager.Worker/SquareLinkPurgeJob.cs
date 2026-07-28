using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker;

/// <summary>
/// Daily job: deletes the Square payment link for any Unpaid Payment older than each team's own
/// Team.PurgeUnpaidLinkDays (default 30). Same 24-hour PeriodicTimer idiom as
/// PaymentReminderJob/DayBeforeReminderJob — not pinned to a specific wall-clock time;
/// SquarePaymentLinkPurgeService's own SquareLinkPurgedUtc tracking field makes an extra same-day
/// tick a no-op rather than a duplicate delete attempt. Looped per Team — each team has its own
/// separate Square merchant account and its own purge threshold (see docs/payment-link-purge.md).
/// </summary>
public class SquareLinkPurgeJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("Jobs:SquareLinkPurgeIntervalHours", 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var purgeService = scope.ServiceProvider.GetRequiredService<SquarePaymentLinkPurgeService>();

            var teams = await dbContext.Teams.ToListAsync(stoppingToken);
            foreach (var team in teams)
            {
                await jobRunHistoryLogger.RunAsync(
                    "SquareLinkPurge",
                    ct => purgeService.RunAsync(team, ct),
                    team.Id,
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
