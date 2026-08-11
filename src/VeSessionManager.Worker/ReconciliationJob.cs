using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Reconciliation;

namespace VeSessionManager.Worker;

/// <summary>
/// Nightly check that ExamTools and this database still agree (built 2026-08-10) — see
/// docs/reconciliation.md.
///
/// <para><b>Why a job and not a test.</b> Every other check in this repo runs against fakes that
/// share our own assumptions. This one needs the real feed and real credentials, so it cannot gate a
/// PR; it is a monitor, and it reports after the fact rather than preventing anything. That is worth
/// having anyway: the bug it was built for — the historical import dropping the last day of every
/// calendar month — had a full suite of passing tests, all of which asserted against a fake that
/// shared the wrong assumption about the date bound.</para>
///
/// <para><b>Per team, one JobRunHistory entry each</b>, matching SessionIngestionJob. One team's
/// expired credentials must not hide another team's clean sweep, and the ops dashboard should be
/// able to say which team drifted.</para>
///
/// <para>Daily. The data it checks changes at most once a session, and a discrepancy that has been
/// true for months is not more urgent for being noticed at noon rather than midnight.</para>
/// </summary>
public class ReconciliationJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReconciliationJob> logger) : BackgroundService
{
    private static readonly JobScheduleDescriptor Descriptor = JobSchedules.For(JobSchedules.Reconciliation);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue(Descriptor.IntervalConfigKey!, Descriptor.DefaultIntervalHours!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            // Required, not optional: an unguarded throw out here stops the whole Worker, not just
            // this job. See JobTick.
            await JobTick.GuardedAsync(logger, "Reconciliation", async () =>
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
                    // One scope per team, not one per tick (issue #292) — same reasoning as
                    // SessionIngestionJob and PerTeamDailyJob: JobRunHistoryLogger clears the tracker
                    // when a team's step fails, and a shared scope makes that every team's problem.
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
                    var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();

                    var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, stoppingToken);
                    if (team is null)
                    {
                        continue;
                    }

                    // The method group binds to the result-returning RunAsync overload, which is what
                    // puts the counts into JobRunHistory.ResultSummary. Binding to the void one would
                    // leave every summary silently null — the overload resolution is load-bearing.
                    await jobRunHistoryLogger.RunAsync(
                        "Reconciliation",
                        ct => service.RunAsync(team, ct),
                        team.Id,
                        stoppingToken);
                }
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
