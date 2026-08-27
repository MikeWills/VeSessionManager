using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// The shapes a trigger can have, and the reason this is a named property rather than something each
/// scanner just happens to do.
/// </summary>
public enum MessageTriggerMechanism
{
    /// <summary>Fires when a stored value changes. The moment is a timestamp already on the row, and there is no parameter.</summary>
    State,

    /// <summary>Fires a configurable number of hours from an anchor instant. <c>MessageRule.ParameterHours</c> is that number.</summary>
    TimeRelative,

    /// <summary>
    /// Not fired at all — offered to a person on a compose screen, who chooses the moment and the
    /// recipients (Mike, 2026-08-21).
    ///
    /// <para>No scanner, no delay, no recipient on the rule. It is a trigger point because that is
    /// what makes its <b>tag list answerable</b>: a message authored without one cannot say which
    /// placeholders apply, since that depends entirely on what sends it.</para>
    ///
    /// <para>⚠️ <c>MessageRuleService</c> must never scan these. A manual trigger with a scanner would
    /// be a mail path nobody asked for — the VE-facing one especially, since every automated path in
    /// this app is candidate- or payment-subject.</para>
    /// </summary>
    Manual
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
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            // Byte-identical to EmailTemplatePlaceholders.ByKey["RegistrationConfirmation"], and it
            // must stay that way for as long as both exist — PR1 froze behaviour, so a token that
            // appears here and not there would be a token the drift test no longer covers.
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "PaymentLinkUrl", "YouthPaymentLinkUrl", "PrivacyPolicyUrl"]),

        new(MessageTrigger.BeforeSessionStart,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Candidate,
            DefaultParameterHours: 24,
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers, MessageRecipient.DiscordChannel],
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "OutstandingPaymentLinkUrl", "PaymentStatus"]),

        new(MessageTrigger.FccFeeOutstanding,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Candidate,
            DefaultParameterHours: 120,
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            Placeholders: ["CandidateName", "SessionDate", "Frn", "FccApplicationFileNumber"]),

        // MessageTrigger.PaymentUnpaid (was here, value 3) is deliberately NOT configurable any more
        // (Mike, 2026-08-25: "PaymentUnpaid is literally worthless. If they didn't pay the test
        // session fee, they couldn't test and/or the VEC would not process it."). Its anchor was the
        // FCC application date, which by definition cannot exist for an unpaid candidate — the FCC
        // never receives an application nobody paid to test for. The enum value stays (old
        // MessageRuleRun history still names it) and Label()/Blurb() still describe it for that
        // history; it is simply absent from this list, so nothing can create a new rule on it — see
        // MessageRuleAdminService.ValidateAsync, which already refuses any trigger not in All.
        //
        // Added 2026-08-25: a candidate who has not paid cannot test, and every existing money
        // trigger anchors on something AFTER the fact (the FCC application, the FCC fee). Nothing
        // warned before the session, while there was still time to do something about it.
        new(MessageTrigger.PaymentUnpaidBeforeSession,
            MessageTriggerMechanism.TimeRelative,
            MessageSubjectType.Payment,
            DefaultParameterHours: 24,
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            Placeholders: ["CandidateName", "SessionDate", "PaymentAmount", "PaymentLinkUrl"]),

        // --- Added in PR3. None of these is seeded: they are things this app could not do before,
        // not reproductions of prior behaviour, so a team opts in by creating a rule. ---

        new(MessageTrigger.CandidateTested,
            MessageTriggerMechanism.State,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "CallSign"]),

        new(MessageTrigger.LicenseGranted,
            MessageTriggerMechanism.State,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            // The first trigger where CallSign resolves to anything — the FCC has issued it by
            // definition. Everywhere earlier it renders blank, which is what the compose screen warns
            // about (#144).
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate", "CallSign", "TeamName"]),

        new(MessageTrigger.FelonyDisclosureDeclared,
            MessageTriggerMechanism.State,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            // Candidate only. This one says "the FCC requires extra steps of you", which is not news
            // to send anywhere but the person it is about.
            LegalRecipients: [MessageRecipient.Candidate, MessageRecipient.TeamAdminAddress, MessageRecipient.SessionLead, MessageRecipient.TeamAdmins, MessageRecipient.SystemAdmins, MessageRecipient.SessionManagers],
            Placeholders: ["CandidateName", "CandidateFirstName", "SessionDate"]),

        // ---- Manual: offered on a compose screen, never scanned ---------------------------------
        //
        // Mike, 2026-08-21: a hand-composed email is a message whose trigger is "somebody pressed a
        // button". Making that a trigger point is what makes the tag list answerable — a message
        // authored without one cannot say which placeholders apply, because that depends on what
        // sends it, and nothing knew yet. That was the whole defect.
        //
        // Placeholders come from the same Names lists the send paths already use, rather than a
        // second copy written out here. Two lists of the same thing is how a tag comes to be offered
        // that renders blank.
        //
        // LegalRecipients is empty and that is not "nobody may receive this" — a manual message is
        // addressed at send time by picking people on the screen, so there is no recipient to choose
        // on the rule.

        new(MessageTrigger.ManualToCandidate,
            MessageTriggerMechanism.Manual,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [],
            Placeholders: CandidatePlaceholderValues.Names),

        new(MessageTrigger.ManualToVe,
            MessageTriggerMechanism.Manual,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [],
            Placeholders: VolunteerExaminerPlaceholderValues.Names),

        // The two per-candidate buttons. Each is its own trigger point rather than sharing one,
        // because a button that sends one particular message is a moment — and its own trigger is
        // what lets the editor show the tags that apply to it, which is the whole reason any of this
        // moved.

        new(MessageTrigger.ManualFelonyDisclosureInstructions,
            MessageTriggerMechanism.Manual,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [],
            Placeholders: CandidatePlaceholderValues.Names),

        new(MessageTrigger.ManualYouthProgramInstructions,
            MessageTriggerMechanism.Manual,
            MessageSubjectType.Candidate,
            DefaultParameterHours: null,
            LegalRecipients: [],
            Placeholders: CandidatePlaceholderValues.Names)
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
