using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// The one definition of which <see cref="MessageRuleOutcome"/>s stop a subject being scanned again —
/// the same shape, and for the same reason, as
/// <c>CandidateApplicationStatusExtensions.TerminalStatuses</c>.
/// </summary>
public static class MessageRuleOutcomes
{
    /// <summary>
    /// Written, and done with. <see cref="MessageRuleOutcome.Suppressed"/> belongs here because a
    /// muted team settles rather than queues: nothing is held while email is off, so re-enabling
    /// starts fresh from that moment instead of flushing days of backlog at once.
    ///
    /// <para>The two absentees are the point. <see cref="MessageRuleOutcome.Failed"/> and
    /// <see cref="MessageRuleOutcome.NoRecipient"/> both describe something that could be different
    /// on the next tick — SMTP recovers, an address gets filled in — so their rows exist as a log and
    /// the subject is scanned again.</para>
    /// </summary>
    public static readonly MessageRuleOutcome[] Terminal = [MessageRuleOutcome.Sent, MessageRuleOutcome.Suppressed];

    public static bool IsTerminal(this MessageRuleOutcome outcome) => Terminal.Contains(outcome);
}
