namespace VeSessionManager.Core.Email;

/// <summary>
/// Plain-English description of what actually causes each template to be sent, surfaced on the
/// Email Templates admin page.
///
/// <para>Editing a template without knowing when it fires is guesswork — and two of these are
/// genuinely surprising: <c>PaymentExpirationNotice</c> goes to the <b>team's admin address, not the
/// candidate</b>, and <c>FelonyDisclosureInstructions</c> is sent automatically when a session is
/// marked completed rather than by a button someone presses.</para>
///
/// <para>Each entry below was read off the sending code, not inferred from the template name. Same
/// hand-maintained arrangement as <see cref="EmailTemplatePlaceholders"/>, and with the same hazard:
/// if a trigger condition changes, this text has to change with it.
/// <c>EmailTemplateTriggersTests</c> only guarantees an entry <i>exists</i> for every seeded key —
/// it cannot tell whether the prose is still true.</para>
/// </summary>
public static class EmailTemplateTriggers
{
    public static readonly IReadOnlyDictionary<string, EmailTemplateTrigger> ByKey = new Dictionary<string, EmailTemplateTrigger>
    {
        ["RegistrationConfirmation"] = new(
            "Automatic",
            "To the candidate",
            "Once, the first time the candidate is picked up from ExamTools — as long as their session is still active and hasn't already happened. " +
            "Can also be re-sent by hand from the candidate's row on Session Detail, which is the one case where it goes out a second time."),

        ["DayBeforeReminder"] = new(
            "Automatic",
            "To the candidate",
            "Once, the day before their session, from the daily reminder job. Includes a payment link only if they still owe a fee."),

        ["PaymentReminder5Day"] = new(
            "Automatic",
            "To the candidate",
            "When their exam fee has been unpaid for 5 days. The clock starts from the date the FCC entered their application, not from the session date — " +
            "so this can legitimately arrive well after a session has finished."),

        ["PaymentExpirationNotice"] = new(
            "Automatic",
            "To your team's admin address — not the candidate",
            "When a fee has been unpaid for 10 days after the FCC entered the application. The payment is marked expired at the same time. " +
            "This one is a heads-up for the team, so it is addressed to the admin notification address in Email Settings."),

        ["FelonyDisclosureInstructions"] = new(
            "Automatic",
            "To the candidate",
            "When a Session Manager marks a session completed, to each candidate in it who both tested and declared a felony disclosure. " +
            "There is no button for this — it rides along with marking the session complete."),

        ["ArrlYouthProgramInstructions"] = new(
            "On demand",
            "To the candidate",
            "Only when someone chooses \"Send youth program instructions\" on a candidate's row. Available only if the session's VEC runs a youth program."),
    };

    public static EmailTemplateTrigger? For(string key) => ByKey.TryGetValue(key, out var trigger) ? trigger : null;
}

/// <param name="Cadence">"Automatic" or "On demand" — the first thing someone editing a template wants to know.</param>
/// <param name="Recipient">Who receives it. Worth stating explicitly because one of these does not go to the candidate.</param>
/// <param name="Description">The actual condition, in plain English.</param>
public sealed record EmailTemplateTrigger(string Cadence, string Recipient, string Description);
