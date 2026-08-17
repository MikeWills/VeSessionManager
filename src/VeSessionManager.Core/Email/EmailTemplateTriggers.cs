namespace VeSessionManager.Core.Email;

/// <summary>
/// Plain-English description of what causes a template to be sent <b>by hand</b>, surfaced on the
/// Email Templates admin page.
///
/// <para><b>Only the on-demand ones live here now (#401, PR2.)</b> This used to describe all seven,
/// including the four the app sent automatically — with their conditions spelled out: "within the
/// next 24 hours", "5 days", "10 days". Those numbers are per-team rules now, so the prose was
/// describing one deployment's defaults as though they were the app's behaviour. Anything automatic is
/// answered by the Message Rules page, which reads the rules themselves and therefore cannot be
/// wrong.</para>
///
/// <para>What is left is genuinely not a rule: somebody opens a screen, picks people, and sends. No
/// row anywhere records when that happens or to whom by default, so a hand-maintained description is
/// the only thing that can explain it — with the same hazard as before, that changing the button
/// means changing this text. <c>EmailTemplateTriggersTests</c> can only check an entry exists, never
/// that its prose is still true.</para>
/// </summary>
public static class EmailTemplateTriggers
{
    public static readonly IReadOnlyDictionary<string, EmailTemplateTrigger> ByKey = new Dictionary<string, EmailTemplateTrigger>
    {
        // Pre-session, and that is the point rather than an oversight: sending it afterwards told
        // someone about an extra FCC step at the point they could no longer easily ask about it.
        ["FelonyDisclosureInstructions"] = new(
            "To the candidate",
            "Only when someone chooses \"Send felony disclosure instructions\" on a candidate's row. Available for any candidate who declared a " +
            "felony disclosure, whether or not they have tested yet — it is usually worth sending before the session, while they can still ask questions. " +
            "Until 2026-08-11 this was sent automatically on marking a session completed."),

        ["ArrlYouthProgramInstructions"] = new(
            "To the candidate",
            "Only when someone chooses \"Send youth program instructions\" on a candidate's row. Available only if the session's VEC runs a youth program."),

        ["GettingStartedLocally"] = new(
            "To the candidate",
            "Never sent automatically. Open a session, choose \"Email candidates\", pick who should get it, and edit the message before sending — " +
            "what goes out is your edited draft, and nothing is written back to this template. " +
            "Note {{CallSign}} is blank until the FCC grants the license, which is usually a few days after the session; the compose screen warns you when that applies to anyone you have picked."),
    };

    /// <summary>
    /// Keys that were seeded by an earlier version and are no longer sent by anything. The rows still
    /// exist in every deployment that ran that version — <c>SeedTemplateIfMissingAsync</c> never
    /// deletes — so the admin page must be able to say so. An editable template nothing sends is
    /// worse than no template: someone maintains it, and nobody receives it.
    ///
    /// <para>Distinct from "no rule references this", which the page also shows and which is a team's
    /// own choice rather than a fact about the code.</para>
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

/// <param name="Recipient">Who receives it.</param>
/// <param name="Description">What actually causes it to be sent, in plain English.</param>
public sealed record EmailTemplateTrigger(string Recipient, string Description);
