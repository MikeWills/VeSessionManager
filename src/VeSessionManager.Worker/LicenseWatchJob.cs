using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Refreshes watched licenses from ExamTools' ULS mirror — see docs/renewal-monitor.md — and, on
/// the same anchored slot, the VE roster's own licenses (issue #107, docs/ve-license-tracking.md).
///
/// <para><b>Anchored, and it shares <c>UlsWatcherJob</c>'s schedule outright</b> — the same
/// <c>SystemSettings.UlsWatcherStartHourEt</c> and <c>UlsWatcherIntervalHours</c>, which default to
/// <b>08:00 ET, every 12 hours</b>. Anchoring replaced a four-hourly tick from Worker start whose
/// check times drifted with every restart: nobody could say when the next check was without knowing
/// when the service last came up.</para>
///
/// <para><b>This paragraph used to say "06:00 ET, once a day", and that the hour was a constant
/// rather than a settings row.</b> Both stopped being true on 2026-08-06, when the renewal monitor
/// was folded onto the watcher's schedule — same data, same source, one schedule — and neither was
/// corrected. The dead <c>JobSchedules.LicenseWatchStartHourEt = 6</c> and a line in CLAUDE.md said
/// the same wrong thing, so all three places anyone would check agreed with each other and disagreed
/// with the code (issue #301, corrected 2026-08-11). The schedule of record is the descriptor in
/// <see cref="JobSchedules"/>, which the Job Schedule page also reads — that is the point of it.</para>
///
/// <para><b>Two JobRunHistory rows from one tick</b>, which is why <see cref="SlotJobNames"/> is
/// overridden. A separate row per half so the ops dashboard can say which one broke — and the slot
/// guard requires a success for both, so a red VeLicenseWatch beside a green LicenseWatch retries
/// on the next hourly tick rather than being read as "this slot is done" (#288). That guard now
/// lives once, in <see cref="AnchoredDailyJob"/>; it was previously a hand-written copy here, and
/// fixing it in one copy is exactly what left the other free to reintroduce the bug.</para>
/// </summary>
public class LicenseWatchJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LicenseWatchJob> logger)
    : AnchoredDailyJob(scopeFactory, timeProvider, logger, JobSchedules.LicenseWatch)
{
    /// <summary>Schedule definition shared with the admin Job Schedule page, so the two cannot drift.</summary>
    private static readonly JobScheduleDescriptor Descriptor = JobSchedules.For(JobSchedules.LicenseWatch);

    /// <inheritdoc/>
    protected override IReadOnlyList<string> SlotJobNames => [JobSchedules.LicenseWatch, JobSchedules.VeLicenseWatch];

    /// <summary>Same SystemSettings values the ULS watcher uses — deliberately one schedule for both (2026-08-06). See the class remarks.</summary>
    protected override async Task<(int StartHourEt, int IntervalHours)> ResolveScheduleAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var settings = await scopedServices.GetRequiredService<SystemSettingsService>().GetAsync(cancellationToken);

        return (JobSchedules.StartHourOrDefault(settings.UlsWatcherStartHourEt, Descriptor.StartHourEt!.Value),
                JobSchedules.IntervalOrDefault(settings.UlsWatcherIntervalHours, Descriptor.DefaultIntervalHours!.Value));
    }

    protected override async Task RunSlotAsync(
        IServiceProvider scopedServices, JobRunHistoryLogger historyLogger, CancellationToken cancellationToken)
    {
        await historyLogger.RunAsync(
            JobSchedules.LicenseWatch,
            scopedServices.GetRequiredService<LicenseWatchService>().RunAsync,
            // Global rather than per-team: one scan covers every team's rows, so there is no single
            // team id to attribute the run to.
            null,
            cancellationToken);

        // The VE roster's own licenses (issue #107), on the same anchored slot: both read the same
        // nightly FCC data through the same mirror, so a second schedule would be two names for one
        // cadence. A separate history row, not a step inside the one above, so the ops dashboard can
        // say which half broke — and SlotJobNames above is what makes the guard honour that.
        await historyLogger.RunAsync(
            JobSchedules.VeLicenseWatch,
            scopedServices.GetRequiredService<VolunteerExaminerLicenseWatchService>().RunAsync,
            null,
            cancellationToken);
    }
}
