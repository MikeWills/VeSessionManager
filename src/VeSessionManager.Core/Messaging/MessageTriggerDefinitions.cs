using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// The two shapes a trigger can have, and the reason this is a named property rather than something
/// each scanner just happens to do.
/// </summary>
public enum MessageTriggerMechanism
{
    /// <summary>Fires when a stored value changes. The moment is a timestamp already on the row, and there is no parameter.</summary>
    State,

    /// <summary>Fires a configurable number of hours from an anchor instant. <c>MessageRule.ParameterHours</c> is that number.</summary>
    TimeRelative
}

/// <summary>
/// What one <see cref="MessageTrigger"/> is: its mechanism, what it fires about, what parameter it
/// takes, who it may address, and which placeholders its scanner supplies.
/// </summary>
/// <param name="DefaultParameterHours">
/// Null for a state trigger. For the three time-relative ones these are today's hardcoded numbers —
/// 24, 120 and 240 — expressed in hours rather than days on purpose (see
/// <c>MessageRule.ParameterHours</c>).
/// </param>
/// <param name="LegalRecipients">
/// Which <see cref="MessageRecipient"/> values make sense for this trigger. A payment expiring is
/// internal news; a registration confirmation addressed to the team's admin inbox is a mistake, not a
/// configuration.
/// </param>
/// <param name="Placeholders">
/// Exactly what this trigger's scanner puts in the dictionary it hands the renderer. Not the tokens a
/// seeded template body happens to use — the same distinction <see cref="Email.EmailTemplatePlaceholders"/>
/// draws, and for the same reason: this list is what an admin can rely on resolving.
/// </param>
public sealed record MessageTriggerDefinition(
    MessageTrigger Trigger,
    MessageTriggerMechanism Mechanism,
    MessageSubjectType SubjectType,
    int? DefaultParameterHours,
    IReadOnlyList<MessageRecipient> LegalRecipients,
    IReadOnlyList<string> Placeholders);

/// <summary>
/// Every trigger point this deployment knows about (#401) — one file, the way
/// <c>Jobs/JobSchedules.cs</c> is one file, and for the same reason: the numbers and the rules used to
/// be literals at the call site that needed them, so a second reader (an admin screen, another
/// service) could only restate them and be wrong the first time one changed.
///
/// <para>Adding a trigger means adding it here <b>and</b> registering an
/// <see cref="IMessageTriggerScanner"/> for it. <see cref="For"/> throws for a trigger with no
/// definition rather than returning a default, so the two cannot drift silently.</para>
/// </summary>
public static class MessageTriggerDefinitions
{
    public static IReadOnlyList<MessageTriggerDefinition> All { get; } =
    [
        new(MessageTrigger.CandidateRegistered,
            MessageTriggerMechanism.State,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [MessageRecipient.Candidate],
            // Byte-identical to EmailTemplatePlaceholders.ByKey["RegistrationConfirmation"], and it
            // must stay that way for as long as both exist — PR1 froze behaviour, so a token that
            // appears here and not there would be a token the drift test no longer covers.
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "PaymentLinkUrl", "YouthPaymentLinkUrl", "PrivacyPolicyUrl"]),

        new(MessageTrigger.BeforeSessionStart,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Candidate,
            DefaultParameterHours: 24,
            LegalRecipients: [MessageRecipient.Candidate],
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "OutstandingPaymentLinkUrl"]),

        new(MessageTrigger.FccFeeOutstanding,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Candidate,
            DefaultParameterHours: 120,
            LegalRecipients: [MessageRecipient.Candidate],
            Placeholders: ["CandidateName", "SessionDate", "Frn", "FccApplicationFileNumber"]),

        new(MessageTrigger.PaymentUnpaid,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Payment,
            DefaultParameterHours: 240,
            // The one trigger whose message was never candidate-facing: it tells the Session Manager
            // that a payment link has gone stale. Candidate is legal too — a team may reasonably want
            // to chase the candidate instead of, or as well as, telling itself.
            LegalRecipients: [MessageRecipient.TeamAdminAddress, MessageRecipient.Candidate],
            Placeholders: ["CandidateName", "SessionDate", "PaymentAmount"])
    ];

    public static MessageTriggerDefinition For(MessageTrigger trigger) =>
        All.FirstOrDefault(d => d.Trigger == trigger)
        ?? throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "No definition is registered for this trigger point.");

    /// <summary>
    /// The parameter a rule actually runs with: its own, or the trigger's default when it has none.
    /// A time-relative rule with a null parameter is a rule somebody created without answering the
    /// only question that trigger asks, so falling back to the default beats refusing to fire.
    /// </summary>
    public static int ParameterHoursOrDefault(MessageRule rule) =>
        rule.ParameterHours ?? For(rule.Trigger).DefaultParameterHours ?? 0;
}
