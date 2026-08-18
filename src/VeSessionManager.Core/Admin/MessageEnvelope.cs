using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// The five envelope settings a rule can carry (#401 PR4), grouped so
/// <c>MessageRuleAdminService.CreateAsync</c> takes one parameter rather than five more.
///
/// <para><b>There is no From here, and that is the design.</b> Changing the From address means SPF,
/// DKIM and DMARC on a domain this app does not control; get it wrong and the mail is silently
/// classed as spam, which is the worst outcome for a reminder nobody knows to expect. Reply-To
/// carries none of that risk and is what "can it come from the session lead" actually means —
/// somebody wants the answer to reach the right person, not the envelope to lie about the
/// sender.</para>
/// </summary>
/// <param name="MonitoringCopyOncePerRun">See <see cref="MessageRule.MonitoringCopyOncePerRun"/> — forty candidates otherwise means forty copies of the same message into one inbox.</param>
public sealed record MessageEnvelope(
    MessageReplyToSource ReplyToSource,
    string? ReplyToOverride,
    string? CcAddress,
    string? BccAddress,
    bool MonitoringCopyOncePerRun)
{
    /// <summary>What every rule had before this existed: the team's own Reply-To, and no copies of its own.</summary>
    public static MessageEnvelope Default { get; } =
        new(MessageReplyToSource.EmailSettings, null, null, null, MonitoringCopyOncePerRun: true);
}
