using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Checks ExamTools' ULS mirror for license grants twice a day — 08:00 and 20:00 ET by default
/// (SystemSettings.UlsWatcherStartHourEt/UlsWatcherIntervalHours).
///
/// <para>Replaced FccDailyWatcherJob + FccWeeklyCatchupJob on 2026-07-31 (see docs/uls-watcher.md).
/// The scheduling machinery is deliberately unchanged from FccDailyWatcherJob — it ticks hourly and
/// asks "has today's most recent due slot already run successfully?" via JobRunHistory, so a Worker
/// that comes up at 08:47 still catches the missed 08:00 slot on its first tick, and later ticks in
/// the same window are skipped. What went away is the *reason* the old job needed a
/// weekly-catchup sibling: an FCC day-name file was a one-shot window that could be missed
/// permanently, whereas a ULS lookup returns current state on every call, so a missed tick costs at
/// most one slot's latency and self-heals. Extra ticks are free — the service only ever touches
/// non-terminal candidates.</para>
///
/// <para>Wall-clock ET rather than "whenever the Worker started" is kept because FCC's own issuance
/// runs at 02:00 ET, so a morning slot lands after that day's grants exist.</para>
///
/// <para>The tick loop and slot guard live in <see cref="AnchoredDailyJob"/> since 2026-08-16
/// (#309, DUP-11) — they were a verbatim second copy of <see cref="LicenseWatchJob"/>'s.</para>
/// </summary>
public class UlsWatcherJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<UlsWatcherJob> logger)
    : AnchoredDailyJob(scopeFactory, timeProvider, logger, JobSchedules.UlsWatcher)
{
    /// <summary>
    /// Shared schedule definition — the admin Job Schedule page reports this job's cadence from the
    /// same descriptor, including the same config keys and defaults, so the two cannot disagree.
    /// </summary>
    private static readonly JobScheduleDescriptor UlsDescriptor = JobSchedules.For(JobSchedules.UlsWatcher);

    /// <summary>
    /// Reads SystemSettings directly, with a fall back to configuration when no settings row exists
    /// yet. Deliberately <b>not</b> switched to <c>SystemSettingsService</c> the way
    /// <see cref="LicenseWatchJob"/> does it: that service materializes defaults, which would
    /// silently change what a deployment with no settings row uses from its configured
    /// <c>Jobs:UlsWatcher*</c> values to the code defaults. Same two settings, different fallback,
    /// and the difference is the reason this stayed per-job when the rest was extracted.
    /// </summary>
    protected override async Task<(int StartHourEt, int IntervalHours)> ResolveScheduleAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var dbContext = scopedServices.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Id == SystemSettingsService.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return (JobSchedules.StartHourOrDefault(settings.UlsWatcherStartHourEt, UlsDescriptor.StartHourEt!.Value),
                    JobSchedules.IntervalOrDefault(settings.UlsWatcherIntervalHours, UlsDescriptor.DefaultIntervalHours!.Value));
        }

        return (
            configuration.GetValue("Jobs:UlsWatcherStartHourEt", UlsDescriptor.StartHourEt!.Value),
            configuration.GetValue(UlsDescriptor.IntervalConfigKey!, UlsDescriptor.DefaultIntervalHours!.Value));
    }

    protected override Task RunSlotAsync(
        IServiceProvider scopedServices, JobRunHistoryLogger historyLogger, CancellationToken cancellationToken) =>
        historyLogger.RunAsync(
            JobSchedules.UlsWatcher,
            scopedServices.GetRequiredService<UlsWatcherService>().RunAsync,
            // Global rather than per-team: one scan covers every team's candidates.
            null,
            cancellationToken);
}
