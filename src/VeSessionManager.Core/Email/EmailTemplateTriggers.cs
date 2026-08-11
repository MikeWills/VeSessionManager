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
            EmailTemplatePhase.AtRegistration,
            "Automatic",
            "To the candidate",
            "Once, the first time the candidate is picked up from ExamTools — as long as their session is still active and hasn't already happened. " +
            "Can also be re-sent by hand from the candidate's row on Session Detail, which is the one case where it goes out a second time."),

        ["DayBeforeReminder"] = new(
            EmailTemplatePhase.PreSession,
            "Automatic",
            "To the candidate",
            "Once, when their session is within the next 24 hours — so roughly a day ahead, wherever they are. " +
            "Includes a payment link only if they still owe a fee."),

        ["FccFeeReminder5Day"] = new(
            EmailTemplatePhase.PostSession,
            "Automatic",
            "To the candidate",
            "When the FCC has been waiting 5 days for its own application fee — the one the candidate pays directly at CORES, not anything owed to your team. " +
            "The clock starts from the date the FCC entered their application, so this can legitimately arrive well after a session has finished. " +
            "Deliberately carries no payment link: the FCC bills the applicant, and your Square link pays a different bill."),

        ["PaymentExpirationNotice"] = new(
            EmailTemplatePhase.PostSession,
            "Automatic",
            "To your team's admin address — not the candidate",
            "When a fee has been unpaid for 10 days after the FCC entered the application. The payment is marked expired at the same time. " +
            "This one is a heads-up for the team, so it is addressed to the admin notification address in Email Settings."),

        ["FelonyDisclosureInstructions"] = new(
            EmailTemplatePhase.PostSession,
            "Automatic",
            "To the candidate",
            "When a Session Manager marks a session completed, to each candidate in it who both tested and declared a felony disclosure. " +
            "There is no button for this — it rides along with marking the session complete."),

        ["ArrlYouthProgramInstructions"] = new(
            EmailTemplatePhase.AtRegistration,
            "On demand",
            "To the candidate",
            "Only when someone chooses \"Send youth program instructions\" on a candidate's row. Available only if the session's VEC runs a youth program."),
    };

    /// <summary>
    /// Keys that were seeded by an earlier version and are no longer sent by anything. The rows still
    /// exist in every deployment that ran that version — <c>SeedTemplateIfMissingAsync</c> never
    /// deletes — so the admin page must be able to say so. An editable template nothing sends is
    /// worse than no template: someone maintains it, and nobody receives it.
    /// </summary>
    public static readonly IReadOnlySet<string> Retired = new HashSet<string>
    {
        // #219: chased the team's exam fee, which is collected at the session and so was never
        // outstanding by the time this could fire. Replaced by FccFeeReminder5Day, about the FCC's
        // own fee.
        "PaymentReminder5Day"
    };

    public static bool IsRetired(string key) => Retired.Contains(key);

    public static EmailTemplateTrigger? For(string key) => ByKey.TryGetValue(key, out var trigger) ? trigger : null;
}

/// <param name="Cadence">"Automatic" or "On demand" — the first thing someone editing a template wants to know.</param>
/// <param name="Recipient">Who receives it. Worth stating explicitly because one of these does not go to the candidate.</param>
/// <param name="Description">The actual condition, in plain English.</param>
public sealed record EmailTemplateTrigger(EmailTemplatePhase Phase, string Cadence, string Recipient, string Description);

/// <summary>
/// Which part of a session's life an email belongs to, used to group the admin page.
///
/// <para>Declaration order is display order — the page sorts on the enum value so the three groups
/// always read in the order the events actually happen.</para>
/// </summary>
public enum EmailTemplatePhase
{
    /// <summary>Around the point a candidate signs up.</summary>
    AtRegistration,

    /// <summary>Between registration and the session itself.</summary>
    PreSession,

    /// <summary>After the exam has been sat — including everything gated on the FCC entering the application, which always happens afterwards.</summary>
    PostSession
}

public static class EmailTemplatePhaseExtensions
{
    public static string Label(this EmailTemplatePhase phase) => phase switch
    {
        EmailTemplatePhase.AtRegistration => "At time of registration",
        EmailTemplatePhase.PreSession => "Pre-session",
        EmailTemplatePhase.PostSession => "Post-session",
        _ => phase.ToString()
    };
}
