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
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
                var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
                var systemSettingsService = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
                var scheduleService = scope.ServiceProvider.GetRequiredService<IngestionScheduleService>();
                var pipeline = scope.ServiceProvider.GetRequiredService<TeamPipeline>();

                // Read fresh every tick (not once at startup like UlsWatcherJob's own interval) —
                // the query is trivial (once per tick, not once per team) and means an admin's edit
                // takes effect on the very next tick, not after a Worker restart.
                var normalIntervalMinutes = (await systemSettingsService.GetAsync(stoppingToken)).SessionIngestionIntervalMinutes;

                var teams = await dbContext.Teams.ToListAsync(stoppingToken);
                foreach (var team in teams)
                {
                    if (!scheduleService.IsDue(team, normalIntervalMinutes, timeProvider.GetUtcNow().UtcDateTime))
                    {
                        continue;
                    }

                    // The step order lives in TeamPipeline, not here — it used to be written out
                    // in this file and twice more in ManualCandidateRefreshService, and the copies
                    // drifted. No job name prefix: these are the scheduled runs the "Manual" ones
                    // are distinguished from.
                    await pipeline.RunAsync(team, jobNamePrefix: string.Empty, onlySessionId: null, stoppingToken);

                    // Deliberately NOT done by the manual refresh: a user-triggered run is extra
                    // work on top of the schedule, not a replacement for it, so it must never delay
                    // the next scheduled poll.
                    team.LastIngestionRunUtc = timeProvider.GetUtcNow().UtcDateTime;
                    await dbContext.SaveChangesAsync(stoppingToken);
                }
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
