namespace VeSessionManager.Core.Payments;

/// <summary>
/// The built-in default for the public youth-rate confirmation page's team-editable first paragraph
/// (2026-08-26) — used both as <c>EmailDefaultsSeeder</c>'s seeded value for a brand-new team's
/// <see cref="Entities.EmailSettings.YouthConfirmIntroHtml"/>, and as the fallback an existing team's
/// null/blank value resolves to. One definition, so the two can never quietly say different things.
/// </summary>
public static class YouthConfirmDefaults
{
    public const string IntroHtml =
        "<p><strong>After you pass, you may be able to claim the FCC license fee back.</strong> " +
        "ARRL runs a youth program that reimburses the FCC application fee for candidates under 18. " +
        "It is separate from the reduced exam fee below, and it is claimed after you have your call " +
        "sign — not now. We will send you the form and instructions once your license is granted.</p>";
}
