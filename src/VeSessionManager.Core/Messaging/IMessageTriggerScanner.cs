using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// One subject a rule is due to fire for, with everything the dispatcher needs and nothing it has to
/// know the trigger to interpret.
/// </summary>
/// <param name="SubjectId">The <see cref="Candidate"/> or <see cref="Payment"/> id — see <paramref name="SubjectType"/>. Half of the idempotency key.</param>
/// <param name="CandidateEmail">
/// The candidate's own address, or null when they have none. Supplied even for a rule addressed to
/// the team's admin inbox, because whether the candidate is reachable is not what decides where an
/// internal notice goes.
/// </param>
/// <param name="Placeholders">Built by the scanner, which is the thing that loaded the graph. The dispatcher passes them straight to <c>EmailTemplateRenderer</c>.</param>
/// <param name="StampLegacySentUtc">
/// Sets whichever <c>Candidate.…SentUtc</c> column this trigger used to own, closing over the tracked
/// entity. Null for a trigger that never had one.
///
/// <para>Those columns are no longer authoritative — <see cref="MessageRuleRun"/> is — but the
/// candidate Email history screen still renders them, so they keep being written. Handing the
/// dispatcher a delegate rather than an enum keeps it from having to know which column belongs to
/// which trigger, which is exactly the knowledge that would rot when the next trigger is added.</para>
/// </param>
public sealed record MessageSubject(
    int SubjectId,
    MessageSubjectType SubjectType,
    string? CandidateEmail,
    IReadOnlyDictionary<string, string> Placeholders,
    Action<DateTime>? StampLegacySentUtc = null);

/// <summary>
/// Answers "which subjects is this rule due to fire for, right now" for one trigger point (#401).
///
/// <para><b>Every scanner owes three things</b>, and each of them was learned the hard way by the
/// hardcoded send it replaces:</para>
/// <list type="number">
/// <item>the guards its predecessor had — the recent-session bound, the payment eligibility window,
/// the PII-purge and cancelled-session exclusions. These belong in the trigger machinery <i>once</i>,
/// not in each rule a team writes;</item>
/// <item>excluding subjects that already have a <b>terminal</b>
/// <see cref="MessageRuleRun"/> for this rule (see <see cref="MessageRuleOutcome"/> — a failed
/// attempt is deliberately not terminal, so it is returned again);</item>
/// <item>bounding by <see cref="MessageRule.CreatedUtc"/>, so a rule never fires for a subject whose
/// moment passed before the rule existed.</item>
/// </list>
/// </summary>
public interface IMessageTriggerScanner
{
    MessageTrigger Trigger { get; }

    /// <param name="onlySessionId">Restrict to one session's subjects — the session-detail refresh button. Null scans the whole team.</param>
    Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team,
        MessageRule rule,
        EmailSettings emailSettings,
        DateTime nowUtc,
        int? onlySessionId,
        CancellationToken cancellationToken);
}
