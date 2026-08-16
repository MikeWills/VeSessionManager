using System.Text.RegularExpressions;

namespace VeSessionManager.Core.Email;

/// <summary>
/// A human name for an <c>EmailTemplate.Key</c> (#144). The admin page shows the raw key, which is
/// fine where it reads as an identifier next to the thing it identifies — but a picker offering
/// "GettingStartedLocally" alongside "RegistrationConfirmation" is asking someone to read code.
///
/// <para><b>These strings get persisted.</b> A send records the label it started from on
/// <c>CandidateEmailSend.TemplateLabel</c>, so history rows keep whatever this returned at the time.
/// Renaming one here leaves the old rows reading the old name, which is the correct outcome — the
/// history says what was actually sent — but it means a rename is not free.</para>
/// </summary>
public static partial class EmailTemplateLabels
{
    private static readonly IReadOnlyDictionary<string, string> ByKey = new Dictionary<string, string>
    {
        ["RegistrationConfirmation"] = "Registration confirmation",
        ["DayBeforeReminder"] = "Day-before reminder",
        ["FccFeeReminder5Day"] = "FCC fee reminder",
        ["PaymentExpirationNotice"] = "Payment expiration notice",
        ["FelonyDisclosureInstructions"] = "Felony disclosure instructions",
        ["ArrlYouthProgramInstructions"] = "Youth program instructions",
        ["GettingStartedLocally"] = "Getting started locally"
    };

    /// <summary>Falls back to splitting the key on its capitals, so a key with no entry here reads as words rather than as nothing.</summary>
    public static string For(string key) =>
        ByKey.TryGetValue(key, out var label) ? label : PascalCaseBoundary().Replace(key, " $1");

    [GeneratedRegex(@"(?<!^)([A-Z])")]
    private static partial Regex PascalCaseBoundary();
}
