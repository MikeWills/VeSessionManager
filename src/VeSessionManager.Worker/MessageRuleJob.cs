using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Worker;

/// <summary>
/// Runs every team's message rules once a day (#401) — the job that replaces DayBeforeReminderJob and
/// takes over the two message passes PaymentReminderJob used to make.
///
/// <para><b>Every trigger, not one.</b> TeamPipeline also runs a rule pass, but scoped to
/// CandidateRegistered and on the ingestion tick; this is the pass that reaches the time-relative
/// triggers. A trigger point is a condition noticed on a scan, not a signal, so an extra tick is a
/// no-op and a missed one catches up — the same reasoning that lets every other daily job here sit on
/// a PeriodicTimer from Worker start rather than a wall clock.</para>
///
/// <para>On <see cref="PerTeamDailyJob"/>, which brings the timer, the per-team scope, the
/// JobRunHistory row and <c>JobTick.GuardedAsync</c> with it.</para>
/// </summary>
public class MessageRuleJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<MessageRuleJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.MessageRule)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<MessageRuleService>().RunAsync(team, onlyTriggers: null, onlySessionId: null, cancellationToken);
}
