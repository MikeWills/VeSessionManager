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
        _ => ""
    };

    /// <summary>The question the hours field is answering, in the form the answer completes.</summary>
    public static string ParameterPrompt(MessageTrigger trigger) => trigger switch
    {
        MessageTrigger.BeforeSessionStart => "Hours before the session starts",
        MessageTrigger.FccFeeOutstanding => "Hours after the FCC entered the application",
        MessageTrigger.PaymentUnpaid => "Hours after the application was entered (or a retest result was marked)",
        _ => "Hours"
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
        MessageRecipient.SessionLead => "The session lead",
        MessageRecipient.DiscordChannel => "A Discord channel",
        _ => recipient.ToString()
    };

    /// <summary>
    /// "24 hours", "5 days" — whichever reads more naturally, since a team setting 120 hours means five
    /// days and should be shown five days without being made to do the arithmetic. Exact multiples
    /// only: 36 hours is 36 hours, not "1.5 days".
    /// </summary>
    public static string DescribeHours(int? hours) => hours switch
    {
        null => "immediately",
        1 => "1 hour",
        var h when h % 24 == 0 && h / 24 == 1 => "1 day",
        var h when h % 24 == 0 => $"{h / 24} days",
        var h => $"{h} hours"
    };
}
