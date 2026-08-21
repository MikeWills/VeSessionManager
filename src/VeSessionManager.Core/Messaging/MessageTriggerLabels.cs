using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// How a trigger point reads on screen (#401, PR2) — kept out of
/// <see cref="MessageTriggerDefinitions"/> for the same reason <c>EmailTemplateLabels</c> is separate
/// from the templates: that registry is what the engine obeys, and prose changing must never look
/// like behaviour changing.
///
/// <para><b>The parameter phrasing is per trigger, not generic.</b> "24 hours before the session"
/// and "5 days after the FCC received the application" are the same field, and rendering both as
/// "after 24 hours" would make one of them a lie — which for a pre-session reminder is the exact
/// mistake the hours-not-dates model exists to prevent.</para>
/// </summary>
public static class MessageTriggerLabels
{
    public static string Label(MessageTrigger trigger) => trigger switch
    {
        MessageTrigger.CandidateRegistered => "When a candidate registers",
        MessageTrigger.BeforeSessionStart => "Before a session starts",
        MessageTrigger.FccFeeOutstanding => "While the FCC is waiting for its fee",
        MessageTrigger.PaymentUnpaid => "While an exam fee is unpaid",
        MessageTrigger.CandidateTested => "When a candidate has tested",
        MessageTrigger.LicenseGranted => "When the FCC grants a license",
        MessageTrigger.FelonyDisclosureDeclared => "When a felony disclosure is declared",
        _ => trigger.ToString()
    };

    public static string Blurb(MessageTrigger trigger) => trigger switch
    {
        MessageTrigger.CandidateRegistered =>
            "Fires once, the first time a candidate is picked up from ExamTools — as long as their session is still active and has not already happened.",
        MessageTrigger.BeforeSessionStart =>
            "Fires once per candidate, the set number of hours ahead of their session start. A duration rather than a time of day, so it means the same thing wherever they are.",
        MessageTrigger.FccFeeOutstanding =>
            "Fires once the FCC has been waiting the set time for its own application fee — the one the candidate pays directly at CORES, never anything owed to you. "
            + "The clock starts from the date the FCC entered the application, which is often not the day they actually received it. That is why this is yours to set.",
        MessageTrigger.PaymentUnpaid =>
            "Fires once an exam fee has gone unpaid for the set time. The payment link is marked expired on the same clock, so changing this changes both.",
        MessageTrigger.CandidateTested =>
            "Fires once a candidate's graded result arrives from ExamTools — the feed is what says somebody tested, not the session being marked completed. "
            + "Note this says nothing about whether they passed.",
        MessageTrigger.LicenseGranted =>
            "Fires once the FCC has granted a license from this session. The only point at which {{CallSign}} resolves to anything — everywhere earlier it renders blank. "
            + "A candidate who was already licensed walking in does not fire this: their grant date predates the session.",
        MessageTrigger.FelonyDisclosureDeclared =>
            "Fires for a candidate who declared a felony conviction on their application, telling them the FCC requires an explanation. "
            + "Worth sending before the session, while there is still someone to ask — which is exactly why it is no longer tied to marking a session completed.",
        _ => ""
    };

    /// <summary>
    /// The question the delay field is answering, in the form the answer completes. Days, because that
    /// is the unit the form takes — the stored value is hours, and <see cref="MessageDelay"/> is where
    /// the two meet.
    /// </summary>
    public static string ParameterPrompt(MessageTrigger trigger) => trigger switch
    {
        MessageTrigger.BeforeSessionStart => "Days before the session starts",
        MessageTrigger.FccFeeOutstanding => "Days after the FCC entered the application",
        MessageTrigger.PaymentUnpaid => "Days after the application was entered (or a retest result was marked)",
        _ => "Days"
    };

    /// <summary>
    /// A caution to show beside the hours field, or null when there is nothing to say.
    ///
    /// <para>Both money triggers are bounded by <c>PaymentEligibilityWindow</c> — 30 days from the
    /// session start — which exists to stop the historical import's backfilled candidates being
    /// chased about payments for sessions they sat months ago. A rule set past that simply stops
    /// firing, silently, and the form's own ceiling is a year. Surfaced rather than refused: the real
    /// headroom depends on how long after the session the FCC entered the application, so there is no
    /// honest number to validate against — only a point past which this stops working.</para>
    /// </summary>
    public static string? ParameterCeilingNote(MessageTrigger trigger) => trigger switch
    {
        MessageTrigger.FccFeeOutstanding or MessageTrigger.PaymentUnpaid =>
            "Past about 30 days from the session this stops firing — payments age out of scope so old sessions are never chased.",
        _ => null
    };

    public static string Label(MessageRecipient recipient) => recipient switch
    {
        MessageRecipient.Candidate => "The candidate",
        MessageRecipient.TeamAdminAddress => "Your team's admin address",
        MessageRecipient.SessionLead => "The session lead (from ExamTools)",
        MessageRecipient.DiscordChannel => "A Discord channel",
        MessageRecipient.TeamAdmins => "Every team admin",
        MessageRecipient.SystemAdmins => "Every system admin",
        MessageRecipient.SessionManagers => "Every session manager",
        _ => recipient.ToString()
    };

    /// <summary>
    /// "1 day", "5 days", "half a day" — the same unit the form takes, so a rule reads back the way it
    /// was written. Halves get words rather than "0.5 days" because that is how somebody says it out
    /// loud, and because a decimal in a list column reads as a precision this is not claiming.
    ///
    /// <para>Anything not landing on a half-day still renders in hours. Nothing can enter one now (see
    /// <see cref="MessageDelay"/>), but rows predating the day field can hold one, and showing "1.7
    /// days" would be a rounding the list is not entitled to make.</para>
    /// </summary>
    public static string DescribeHours(int? hours) => hours switch
    {
        null => "immediately",
        1 => "1 hour",
        12 => "half a day",
        var h when h % 24 == 0 && h / 24 == 1 => "1 day",
        var h when h % 24 == 0 => $"{h / 24} days",
        var h when h % 12 == 0 => $"{h / 24}½ days",
        var h => $"{h} hours"
    };
}
