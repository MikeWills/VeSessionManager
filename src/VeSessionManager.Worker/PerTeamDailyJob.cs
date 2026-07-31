using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Shared scaffold extracted 2026-07-29 after a duplicate-code review found PaymentReminderJob,
/// SquareLinkPurgeJob, and DayBeforeReminderJob had each independently reimplemented the identical
/// "24h PeriodicTimer, load every Team, JobRunHistoryLogger.RunAsync per team" shape — differing
/// only in the job name, interval config key/default, and which service's method to call per team.
///
/// Deliberately NOT used by every Worker job: PiiPurgeJob is global rather than per-team, and
/// UlsWatcherJob is wall-clock-pinned (US Eastern) with its own hourly catch-up logic — neither
/// shares this shape, so they stay hand-written rather than being forced into it.
/// </summary>
public abstract class PerTeamDailyJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    string jobName,
    string intervalConfigKey,
    int defaultIntervalHours) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue(intervalConfigKey, defaultIntervalHours);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

            var teams = await dbContext.Teams.ToListAsync(stoppingToken);
            foreach (var team in teams)
            {
                await jobRunHistoryLogger.RunAsync(
                    jobName,
                    ct => RunForTeamAsync(scope.ServiceProvider, team, ct),
                    team.Id,
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Resolve this job's specific service from scopedServices and run it for the given team.</summary>
    protected abstract Task RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken);
}
