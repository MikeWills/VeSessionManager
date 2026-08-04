using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker;

/// <summary>
/// Daily job: deletes the Square payment link for any Unpaid Payment older than each team's own
/// Team.PurgeUnpaidLinkDays (default 30). Same 24-hour PeriodicTimer idiom as
/// PaymentReminderJob/DayBeforeReminderJob (via PerTeamDailyJob) — not pinned to a specific
/// wall-clock time; SquarePaymentLinkPurgeService's own SquareLinkPurgedUtc tracking field makes an
/// extra same-day tick a no-op rather than a duplicate delete attempt. Looped per Team — each team
/// has its own separate Square merchant account and its own purge threshold (see
/// docs/payment-link-purge.md).
/// </summary>
public class SquareLinkPurgeJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SquareLinkPurgeJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, "SquareLinkPurge", "Jobs:SquareLinkPurgeIntervalHours", 24)
{
    protected override Task RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        scopedServices.GetRequiredService<SquarePaymentLinkPurgeService>().RunAsync(team, cancellationToken);
}
