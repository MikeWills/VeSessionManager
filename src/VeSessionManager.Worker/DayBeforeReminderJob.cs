using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 4's "daily job" (per the spec, distinct from the ~5-minute ingestion/scheduling/payment
/// tick) that finds candidates whose session is tomorrow and sends the reminder. Runs on a
/// 24-hour PeriodicTimer starting from whenever the Worker process starts, not pinned to a
/// specific wall-clock time of day — simplest option consistent with every other job in this
/// codebase (PeriodicTimer, no cron/Quartz dependency, via PerTeamDailyJob); acceptable since
/// CandidateNotificationService's send-once tracking makes an extra same-day tick a no-op rather
/// than a duplicate send. Looped per Team — each team has its own SMTP account (multi-team, see
/// docs/multi-team.md).
/// </summary>
public class DayBeforeReminderJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DayBeforeReminderJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.DayBeforeReminder)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<CandidateNotificationService>().SendDayBeforeRemindersAsync(team, cancellationToken);
}
