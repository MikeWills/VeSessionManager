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
    ILogger logger,
    string jobName) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Interval comes from the shared registry rather than a literal here, so the admin Job
        // Schedule page and this timer can never disagree about how often the job runs.
        var descriptor = JobSchedules.For(jobName);
        var intervalHours = configuration.GetValue(descriptor.IntervalConfigKey!, descriptor.DefaultIntervalHours!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            await JobTick.GuardedAsync(logger, jobName, async () =>
            {
                // A short-lived scope just to list the teams, closed before any per-team work.
                List<int> teamIds;
                using (var tickScope = scopeFactory.CreateScope())
                {
                    var tickDbContext = tickScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    teamIds = await tickDbContext.Teams.Select(t => t.Id).ToListAsync(stoppingToken);
                }

                foreach (var teamId in teamIds)
                {
                    // One scope per team, not one per tick (issue #292). A shared scope kept every
                    // team's materialized graph tracked for the whole run, and — the part that
                    // actually bites — JobRunHistoryLogger clears the tracker when a team's step
                    // fails, discarding the pending state of every *other* team in the same scope.
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

                    var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, stoppingToken);
                    if (team is null)
                    {
                        // Deleted between the two queries. Nothing to do, and not worth a warning.
                        continue;
                    }

                    await jobRunHistoryLogger.RunAsync(
                        jobName,
                        ct => RunForTeamAsync(scope.ServiceProvider, team, ct),
                        team.Id,
                        stoppingToken);
                }
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Resolve this job's specific service from scopedServices and run it for the given team.</summary>
    protected abstract Task RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken);
}
