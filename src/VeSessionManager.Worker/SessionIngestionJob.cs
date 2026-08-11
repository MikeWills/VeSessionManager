using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Worker;

/// <summary>
/// Polls ExamTools on a timer, runs the Session/Candidate ingestion diff (Phase 1) once per Team
/// (multi-team foundation — see docs/multi-team.md), then Phase 7's VE roster sync (reuses the same
/// ExamTools credentials, no reason to wait for a separate tick), then ExamResultSyncService's
/// graded-exam-result auto-detection (added 2026-07-28 — see that class's own doc comment), then —
/// per the spec's "hook this into the end of Phase 1's new session detected path" — runs Phase 2's
/// Zoom/Discord scheduling pass, Phase 3's Square payment-link generation, and Phase 4's
/// registration-confirmation email in the same tick, in that order: by the time confirmations go
/// out, the session's Zoom link and (if the VEC collects a fee) the candidate's payment link have
/// had their best chance to already exist, so the email reads as complete rather than partial.
///
/// Every step is looped per Team — each has its own ExamTools/Zoom/Square/SMTP credentials; Discord
/// shares one bot but each team picks its own Guild (see docs/multi-team.md). Each step still gets
/// its own JobRunHistory entry: a failure in one step (or one team's turn) shouldn't read as a
/// failure in another on the ops dashboard, and each later step should still run against whatever
/// the earlier ones already committed even if it itself later fails.
///
/// **Self-throttling (added after launch, surge behavior removed post-launch — see CLAUDE.md):** the
/// whole per-team block above only actually runs when IngestionScheduleService.IsDue says so — most
/// teams run a session once a day or less, so hitting ExamTools every tick around the clock has no
/// real upside almost all the time. SystemSettings.SessionIngestionIntervalMinutes
/// (SystemAdmin-configurable, default 60) is the flat cadence for every team — this job no longer
/// "surges" back to its own tick cadence near a session's start time; that's now a user-triggered
/// "Refresh candidates" button on the session detail page instead (ManualCandidateRefreshService),
/// so a Session Manager who needs a last-minute registrant pulled in right now doesn't have to wait
/// on the poll. The whole block is gated together (not just the ExamTools-touching steps) — a
/// deliberate simplicity tradeoff, see CLAUDE.md. Team.LastIngestionRunUtc is the bookkeeping field
/// this reads/writes; skipped teams get no JobRunHistory entries that tick.
/// </summary>
public class SessionIngestionJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SessionIngestionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var descriptor = JobSchedules.For(JobSchedules.SessionIngestion);
        var intervalSeconds = configuration.GetValue(descriptor.IntervalConfigKey!, descriptor.DefaultIntervalSeconds!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            await JobTick.GuardedAsync(logger, "SessionIngestion", async () =>
            {
                // A short-lived scope just to answer "which teams, and how often" — closed before any
                // per-team work begins, so nothing it tracked can outlive it.
                List<int> teamIds;
                int normalIntervalMinutes;
                using (var tickScope = scopeFactory.CreateScope())
                {
                    var tickDbContext = tickScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var systemSettingsService = tickScope.ServiceProvider.GetRequiredService<SystemSettingsService>();

                    // Read fresh every tick (not once at startup like UlsWatcherJob's own interval) —
                    // the query is trivial (once per tick, not once per team) and means an admin's
                    // edit takes effect on the very next tick, not after a Worker restart.
                    normalIntervalMinutes = (await systemSettingsService.GetAsync(stoppingToken)).SessionIngestionIntervalMinutes;
                    teamIds = await tickDbContext.Teams.Select(t => t.Id).ToListAsync(stoppingToken);
                }

                foreach (var teamId in teamIds)
                {
                    // **One scope per team, not one per tick (issue #292).** With a single shared
                    // scope, everything team A's six pipeline steps materialized stayed tracked
                    // through teams B and C — DetectChanges walking a growing graph on every one of
                    // the deliberate per-item saves — and, worse, JobRunHistoryLogger's failure-path
                    // ChangeTracker.Clear() discarded *their* pending state too. A bad row in the
                    // first team is now contained to that team.
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
                    var scheduleService = scope.ServiceProvider.GetRequiredService<IngestionScheduleService>();
                    var pipeline = scope.ServiceProvider.GetRequiredService<TeamPipeline>();

                    var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, stoppingToken);
                    if (team is null)
                    {
                        // Deleted between the two queries. Nothing to do, and not worth a warning.
                        continue;
                    }

                    if (!scheduleService.IsDue(team, normalIntervalMinutes, timeProvider.GetUtcNow().UtcDateTime))
                    {
                        continue;
                    }

                    // The step order lives in TeamPipeline, not here — it used to be written out
                    // in this file and twice more in ManualCandidateRefreshService, and the copies
                    // drifted. No job name prefix: these are the scheduled runs the "Manual" ones
                    // are distinguished from.
                    await pipeline.RunAsync(team, jobNamePrefix: string.Empty, onlySessionId: null, stoppingToken);

                    // **ExecuteUpdateAsync, not a tracked write (issue #232).** This is the throttle
                    // stamp, and it used to be `team.LastIngestionRunUtc = …; SaveChangesAsync()`.
                    // Any failed pipeline step above calls JobRunHistoryLogger's
                    // ChangeTracker.Clear(), which DETACHES `team` — so the assignment landed on a
                    // detached object and the save wrote nothing. Silently: no exception, and the
                    // job's own history row still recorded the step failure rather than this.
                    //
                    // The effect was that IngestionScheduleService.IsDue returned true on every
                    // 300-second tick instead of every SessionIngestionIntervalMinutes, for that
                    // team, forever — the self-throttling simply off, with nothing saying so, and
                    // the Job Schedule page reporting a last-run time that never advanced.
                    //
                    // An UPDATE straight to the database cannot be undone by the tracker, so this is
                    // immune by construction rather than by remembering to re-attach. Scoping per
                    // team (above) narrows the blast radius but does NOT fix this on its own: the
                    // clear happens during this team's own pipeline.
                    //
                    // Deliberately NOT done by the manual refresh: a user-triggered run is extra
                    // work on top of the schedule, not a replacement for it, so it must never delay
                    // the next scheduled poll.
                    var ranUtc = timeProvider.GetUtcNow().UtcDateTime;
                    await dbContext.Teams
                        .Where(t => t.Id == teamId)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastIngestionRunUtc, ranUtc), stoppingToken);
                }
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
