using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;

using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 6's daily job, down to one pass since 2026-08-25: stale-Unmatched review flags. Same
/// 24-hour PeriodicTimer idiom as DayBeforeReminderJob/UlsWatcherJob (via PerTeamDailyJob) — not
/// pinned to a specific wall-clock time; Candidate.UnmatchedReviewFlaggedUtc makes an extra
/// same-day tick a no-op rather than a duplicate flag. Looped per Team — each team has its own
/// SMTP account (multi-team, see docs/multi-team.md).
/// </summary>
public class PaymentReminderJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<PaymentReminderJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.PaymentReminder)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<PaymentReminderService>().RunAsync(team, cancellationToken);
}
