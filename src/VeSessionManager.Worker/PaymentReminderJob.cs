using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;

using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 6's daily job: 5-day payment reminders, 10-day expiration notices, and stale-Unmatched
/// review flags. Same 24-hour PeriodicTimer idiom as DayBeforeReminderJob/UlsWatcherJob (via
/// PerTeamDailyJob) — not pinned to a specific wall-clock time; PaymentReminderService's own
/// tracking fields (PaymentReminderSentUtc/ExpiredUnpaid/UnmatchedReviewFlaggedUtc) make an extra
/// same-day tick a no-op rather than a duplicate send/flag. Looped per Team — each team has its own
/// SMTP account (multi-team, see docs/multi-team.md).
/// </summary>
public class PaymentReminderJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<PaymentReminderJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.PaymentReminder)
{
    protected override Task RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        scopedServices.GetRequiredService<PaymentReminderService>().RunAsync(team, cancellationToken);
}
