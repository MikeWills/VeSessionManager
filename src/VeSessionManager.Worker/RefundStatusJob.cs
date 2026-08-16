using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker;

/// <summary>
/// Checks submitted Square refunds for a terminal outcome, and re-sends any whose original call
/// never came back (#375). See <see cref="RefundStatusService"/> for why a refund needs following at
/// all — Square answers immediately and can take up to 14 days to actually settle.
///
/// <para>Hourly rather than the daily cadence the other Square job uses. Most refunds finish within
/// a few hours, and this is money a candidate is waiting on: a daily check means a refund that
/// bounced at 9am is not noticed until the next morning. The work is a no-op whenever nothing is
/// in flight, which is nearly always — the scan is one indexed query per team.</para>
/// </summary>
public class RefundStatusJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<RefundStatusJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.RefundStatus)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<RefundStatusService>().RunAsync(team, cancellationToken);
}
