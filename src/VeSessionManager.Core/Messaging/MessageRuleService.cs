using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// One team's rule pass (#401): load the enabled rules, ask each one's scanner who is due, hand the
/// answer to <see cref="MessageDispatchService"/>.
///
/// <para><b>Scan-based and idempotent, like every other job here.</b> Nothing about trigger points is
/// event-driven — a trigger is not a signal that fires once, it is a condition this pass notices has
/// become true, and the <see cref="MessageRuleRun"/> marker is what keeps noticing it from happening
/// twice. A tick missed while the Worker was down costs nothing.</para>
/// </summary>
public class MessageRuleService(
    AppDbContext dbContext,
    IEnumerable<IMessageTriggerScanner> scanners,
    MessageDispatchService dispatchService,
    TimeProvider timeProvider,
    ILogger<MessageRuleService> logger)
{
    /// <param name="onlyTriggers">
    /// Restrict the pass to these trigger points, or null for all of them. <c>TeamPipeline</c> passes
    /// <see cref="MessageTrigger.CandidateRegistered"/> alone: the pipeline runs on the ~5-minute
    /// ingestion tick and from the session-detail refresh button, and running every trigger there
    /// would quietly move the pre-session reminder off its daily job and onto whenever somebody
    /// pressed refresh.
    /// </param>
    /// <param name="onlySessionId">Restrict to one session's subjects — the session-detail refresh button. Null scans the whole team.</param>
    public async Task<MessageRuleResult> RunAsync(
        Team team, IReadOnlyCollection<MessageTrigger>? onlyTriggers, int? onlySessionId, CancellationToken cancellationToken)
    {
        var result = new MessageRuleResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var rules = await dbContext.MessageRules
            .Where(r => r.TeamId == team.Id && r.IsEnabled && (onlyTriggers == null || onlyTriggers.Contains(r.Trigger)))
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
        {
            return result;
        }

        // Loaded once for the whole pass rather than per rule: it is one row per team, every rule
        // needs it, and a team without one cannot send anything at all.
        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            logger.LogWarning("No EmailSettings row exists yet for team {TeamId} — skipping all {RuleCount} message rule(s) until seeded", team.Id, rules.Count);
            return result;
        }

        foreach (var rule in rules)
        {
            var scanner = scanners.FirstOrDefault(s => s.Trigger == rule.Trigger);
            if (scanner is null)
            {
                // A trigger with a definition but no scanner. Logged as an error rather than skipped:
                // it means a rule an admin created and can see enabled on screen does nothing at all,
                // which is indistinguishable from working until somebody asks why nobody was emailed.
                logger.LogError("No scanner is registered for trigger {Trigger} — rule \"{RuleName}\" ({RuleId}) on team {TeamId} cannot run",
                    rule.Trigger, rule.Name, rule.Id, team.Id);
                continue;
            }

            var subjects = await scanner.ScanAsync(team, rule, emailSettings, now, onlySessionId, cancellationToken);
            result.Add(await dispatchService.DispatchAsync(team, rule, emailSettings, subjects, cancellationToken));
        }

        logger.LogInformation("Message rule pass finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }
}
