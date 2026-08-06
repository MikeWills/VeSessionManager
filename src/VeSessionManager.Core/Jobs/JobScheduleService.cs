using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.Jobs;

/// <summary>How confident the app is about a job's stated next-run time.</summary>
public enum NextRunConfidence
{
    /// <summary>Wall-clock anchored: the time is what will happen, restart or no restart.</summary>
    Scheduled,

    /// <summary>
    /// Derived as "last run + interval". Correct while the Worker stays up, but the timer restarts
    /// its cycle whenever the process does, so a restart moves it.
    /// </summary>
    Estimated,

    /// <summary>
    /// An anchored job whose current slot has no successful run yet — it is behind and will catch up
    /// on its next hourly tick rather than waiting for the following slot.
    /// </summary>
    DueNow,

    /// <summary>Never run, so there is no interval to count from. Only possible for the estimated kind.</summary>
    Unknown
}

/// <param name="LastRunUtc">Most recent run of any outcome — the honest "when did this last happen".</param>
/// <param name="LastSuccessUtc">
/// Most recent *successful* run. Tracked separately because anchored jobs decide their catch-up from
/// success alone, and because a job failing every attempt still has a recent LastRunUtc.
/// </param>
public sealed record JobScheduleStatus(
    JobScheduleDescriptor Descriptor,
    string CadenceSummary,
    DateTime? LastRunUtc,
    DateTime? LastSuccessUtc,
    bool LastRunSucceeded,
    DateTime? NextRunUtc,
    NextRunConfidence Confidence);

/// <summary>
/// Backs the admin Job Schedule page: what every background job is, how often it runs, when it last
/// ran and when it runs next.
///
/// <para><b>Why this is worth a screen.</b> "When does X run next?" was previously answerable only by
/// reading the Worker's source, and only for ingestion was it visible anywhere in the UI (Team
/// Maintenance, per team). Job History answers "did it run", never "will it".</para>
///
/// <para><b>It reports rather than decides.</b> Intervals come from <see cref="JobSchedules"/> and the
/// same configuration keys and SystemSettings row the Worker obeys — no number is re-typed here.
/// A page that invented its own copy would drift the first time anyone retuned a job, and would do it
/// silently, which is worse than not having the page.</para>
/// </summary>
public class JobScheduleService(AppDbContext dbContext, IConfiguration configuration, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<JobScheduleStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var jobNames = JobSchedules.All.Select(d => d.JobName).ToList();

        // One grouped query rather than one per job. Manual runs are excluded by the exact-name match:
        // TeamPipeline prefixes user-triggered runs with "Manual", and those say nothing about the
        // schedule — counting one would report a job as freshly run when its timer had not fired.
        var lastRuns = await dbContext.JobRunHistories
            .Where(h => jobNames.Contains(h.JobName))
            .GroupBy(h => h.JobName)
            .Select(g => new
            {
                JobName = g.Key,
                LastRunUtc = (DateTime?)g.Max(h => h.StartedUtc),
                // The cast goes INSIDE the Max, not around it: a job whose every run failed has an
                // empty filtered sequence, and Max over that throws rather than yielding null. The
                // outer cast doesn't help — the exception happens before there is anything to cast.
                // That failure would have taken down the whole page for one perpetually-failing job.
                LastSuccessUtc = g.Where(h => h.Success).Max(h => (DateTime?)h.StartedUtc)
            })
            .ToListAsync(cancellationToken);

        var lastRunByJob = lastRuns.ToDictionary(r => r.JobName);

        // The one job whose schedule is tunable at runtime rather than only in configuration.
        var settings = await dbContext.SystemSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var nowEt = DailySlotSchedule.NowEastern(timeProvider);

        return JobSchedules.All
            .Select(descriptor =>
            {
                lastRunByJob.TryGetValue(descriptor.JobName, out var run);
                var lastRunUtc = run?.LastRunUtc;
                var lastSuccessUtc = run?.LastSuccessUtc;

                return descriptor.Kind == JobCadenceKind.AnchoredToEasternHour
                    ? BuildAnchored(descriptor, settings, nowEt, lastRunUtc, lastSuccessUtc)
                    : BuildInterval(descriptor, nowUtc, lastRunUtc, lastSuccessUtc);
            })
            .ToList();
    }

    private JobScheduleStatus BuildAnchored(
        JobScheduleDescriptor descriptor,
        Entities.SystemSettings? settings,
        DateTime nowEt,
        DateTime? lastRunUtc,
        DateTime? lastSuccessUtc)
    {
        // Resolution order mirrors UlsWatcherJob's own: the SystemSettings row wins over configuration,
        // configuration over the built-in default. LicenseWatch is not settings-backed, so it skips
        // straight past the first two.
        var intervalHours = descriptor.SettingsBacked && settings is not null
            ? settings.UlsWatcherIntervalHours
            : ResolveIntervalHours(descriptor);
        var startHourEt = descriptor.SettingsBacked && settings is not null
            ? settings.UlsWatcherStartHourEt
            : descriptor.StartHourEt ?? 0;

        var dueSlotUtc = DailySlotSchedule.LatestDueSlotUtc(nowEt, startHourEt, intervalHours);

        // Exactly the test the job itself performs each tick: a slot with no successful run is not
        // "next", it is overdue, and the job picks it up within the hour rather than skipping a day.
        var currentSlotRan = lastSuccessUtc is { } success && success >= dueSlotUtc;

        return new JobScheduleStatus(
            descriptor,
            DescribeAnchored(startHourEt, intervalHours),
            lastRunUtc,
            lastSuccessUtc,
            LastRunSucceeded: lastRunUtc is null || lastSuccessUtc == lastRunUtc,
            NextRunUtc: currentSlotRan ? DailySlotSchedule.NextSlotUtc(nowEt, startHourEt, intervalHours) : null,
            Confidence: currentSlotRan ? NextRunConfidence.Scheduled : NextRunConfidence.DueNow);
    }

    private JobScheduleStatus BuildInterval(
        JobScheduleDescriptor descriptor,
        DateTime nowUtc,
        DateTime? lastRunUtc,
        DateTime? lastSuccessUtc)
    {
        var interval = ResolveInterval(descriptor);

        // Counting from the last run, not from now: the timer fires on a fixed cycle, and the last
        // run marks where in that cycle we are. Null when it has never run — there is nothing to
        // count from, and guessing would be inventing an answer.
        var nextRunUtc = lastRunUtc is { } last ? last + interval : (DateTime?)null;

        return new JobScheduleStatus(
            descriptor,
            DescribeInterval(interval),
            lastRunUtc,
            lastSuccessUtc,
            LastRunSucceeded: lastRunUtc is null || lastSuccessUtc == lastRunUtc,
            // A due-in-the-past estimate means the Worker restarted (resetting the cycle) or is down.
            // Reported as-is rather than rolled forward — a stale timestamp is a signal worth seeing.
            NextRunUtc: nextRunUtc,
            Confidence: nextRunUtc is null ? NextRunConfidence.Unknown : NextRunConfidence.Estimated);
    }

    private int ResolveIntervalHours(JobScheduleDescriptor descriptor) =>
        descriptor.IntervalConfigKey is { } key
            ? configuration.GetValue(key, descriptor.DefaultIntervalHours ?? 24)
            : descriptor.DefaultIntervalHours ?? 24;

    private TimeSpan ResolveInterval(JobScheduleDescriptor descriptor)
    {
        if (descriptor.DefaultIntervalSeconds is { } defaultSeconds)
        {
            var seconds = descriptor.IntervalConfigKey is { } secondsKey
                ? configuration.GetValue(secondsKey, defaultSeconds)
                : defaultSeconds;
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromHours(ResolveIntervalHours(descriptor));
    }

    private static string DescribeAnchored(int startHourEt, int intervalHours)
    {
        var runsPerDay = Math.Max(1, 24 / intervalHours);
        var hours = Enumerable.Range(0, runsPerDay)
            .Select(i => DateTime.Today.AddHours((startHourEt + i * intervalHours) % 24).ToString("h:mm tt"))
            .ToList();
        return $"{string.Join(" and ", hours)} Eastern";
    }

    private static string DescribeInterval(TimeSpan interval) => interval switch
    {
        { TotalHours: >= 24 } when interval.TotalHours % 24 == 0 =>
            interval.TotalHours == 24 ? "Every 24 hours" : $"Every {interval.TotalDays:0} days",
        { TotalHours: >= 1 } => $"Every {interval.TotalHours:0} hours",
        _ => $"Every {interval.TotalMinutes:0} minutes"
    };
}
