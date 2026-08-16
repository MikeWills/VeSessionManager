using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Shared scaffold for the jobs pinned to a wall-clock hour in US Eastern rather than to an interval
/// from Worker start — extracted 2026-08-16 (#309, DUP-11) after <see cref="UlsWatcherJob"/> and
/// <see cref="LicenseWatchJob"/> were found to have independently reimplemented the identical
/// hourly-tick-plus-slot-guard shape, sharing a verbatim comment.
///
/// <para><b>The shape.</b> Tick hourly; work out the most recent due slot in Eastern time; skip if
/// that slot has already been run successfully. That is what makes an anchored schedule survive
/// restarts and outages — a Worker that boots at 08:47 finds no successful run since today's 08:00
/// slot and runs it immediately, and every later tick that day finds one and skips. Contrast
/// <see cref="PerTeamDailyJob"/>, whose timer starts when the process does and whose run times
/// therefore drift with every restart.</para>
///
/// <para><b>The guard requires a success for <i>every</i> name in <see cref="SlotJobNames"/>, and
/// that generalization is the point of extracting this.</b> <see cref="LicenseWatchJob"/> writes two
/// JobRunHistory rows from one tick, and its copy of this guard originally checked only the first —
/// so a green LicenseWatch beside a red VeLicenseWatch was read as "this slot is done" and the
/// failing half never retried that day (#288). Fixing it in one copy left the other copy free to
/// reintroduce it. Here there is one copy, and a job that writes two rows says so by listing two
/// names.</para>
///
/// <para>Deliberately not used by every job: <see cref="SessionIngestionJob"/> and
/// <see cref="HistoricalImportJob"/> poll far more often than hourly and have no slot to anchor to,
/// and everything on <see cref="PerTeamDailyJob"/> is per-team rather than global.</para>
/// </summary>
/// <param name="jobName">
/// Must match the string passed to <c>JobRunHistoryLogger.RunAsync</c> exactly — it is the join key
/// back to <c>JobRunHistory</c>, which is where "has this slot already run?" is answered.
/// </param>
public abstract class AnchoredDailyJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger logger,
    string jobName) : BackgroundService
{
    /// <summary>
    /// How often the <i>slot check</i> runs, not how often the work happens. Hourly so a Worker that
    /// boots after the anchor hour picks up the missed slot within the hour rather than waiting a
    /// whole cycle.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Every JobRunHistory name this job's tick writes. The slot counts as done only once all of
    /// them have a success in it — see the class remarks and #288. Defaults to just this job's own
    /// name, which is right for a tick that writes one row.
    /// </summary>
    protected virtual IReadOnlyList<string> SlotJobNames => [jobName];

    /// <summary>When this job's slots fall, resolved per tick because an admin can change it at runtime.</summary>
    protected abstract Task<(int StartHourEt, int IntervalHours)> ResolveScheduleAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken);

    /// <summary>The work, once the guard has decided this slot is due. Implementations log their own JobRunHistory rows, one per name in <see cref="SlotJobNames"/>.</summary>
    protected abstract Task RunSlotAsync(
        IServiceProvider scopedServices, JobRunHistoryLogger historyLogger, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        do
        {
            // Required, not optional: without it a transient "database is locked" from the shared
            // SQLite file would stop the entire Worker, not just this job. See JobTick.
            await JobTick.GuardedAsync(logger, jobName, () => RunTickAsync(stoppingToken));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One iteration, separated from the timer loop so it can be driven directly by a test (#325).
    /// The loop above is three lines of framework usage; every bug these jobs have had lived here.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var (startHourEt, intervalHours) = await ResolveScheduleAsync(scopedServices, stoppingToken);

        var nowEt = DailySlotSchedule.NowEastern(timeProvider);
        var dueSlotUtc = DailySlotSchedule.LatestDueSlotUtc(nowEt, startHourEt, intervalHours);

        var dbContext = scopedServices.GetRequiredService<AppDbContext>();
        var names = SlotJobNames;

        // TeamId == null is not a narrowing — these jobs are global and always log with teamId null,
        // so the null-team rows are exactly the ones being counted. It is here to make the
        // (TeamId, JobName, StartedUtc) index seekable: without a leading TeamId predicate SQLite
        // cannot use it and this scans the whole table, hourly (#296).
        var succeededThisSlot = await dbContext.JobRunHistories
            .Where(h => h.TeamId == null && names.Contains(h.JobName) && h.Success && h.StartedUtc >= dueSlotUtc)
            .Select(h => h.JobName)
            .Distinct()
            .CountAsync(stoppingToken);

        if (succeededThisSlot == names.Count)
        {
            // `return`, not `continue` — this is the guarded tick body; returning ends this tick and
            // the loop waits for the next hourly one.
            return;
        }

        await RunSlotAsync(scopedServices, scopedServices.GetRequiredService<JobRunHistoryLogger>(), stoppingToken);
    }
}
