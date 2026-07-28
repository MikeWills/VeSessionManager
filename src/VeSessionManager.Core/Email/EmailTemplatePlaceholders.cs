namespace VeSessionManager.Core.Email;

/// <summary>
/// Phase 9c: registry of which {{Placeholder}} tokens each EmailTemplate.Key actually gets
/// substituted at send time — hand-collected from the real Dictionary&lt;string,string&gt; literals
/// in CandidateNotificationService/PaymentReminderService (the only two places templates are ever
/// rendered), not the seeded template body text (which is just a starting example an admin is
/// expected to rewrite). Surfaced by the EmailTemplates admin page so an admin editing a template
/// knows which tokens are actually available for that Key. EmailTemplatePlaceholdersTests.cs
/// guards against this drifting out of sync with the sending code.
/// </summary>
public static class EmailTemplatePlaceholders
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByKey = new Dictionary<string, IReadOnlyList<string>>
    {
        ["RegistrationConfirmation"] = ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "PaymentLinkUrl", "YouthPaymentLinkUrl", "PrivacyPolicyUrl"],
        ["DayBeforeReminder"] = ["CandidateName", "CandidateFirstName", "SessionDate", "ZoomJoinUrl", "OutstandingPaymentLinkUrl"],
        ["PaymentReminder5Day"] = ["CandidateName", "ZoomJoinUrl", "PaymentLinkUrl"],
        ["PaymentExpirationNotice"] = ["CandidateName", "SessionDate", "PaymentAmount"],
        ["FelonyDisclosureInstructions"] = ["CandidateName"],
        ["ArrlYouthProgramInstructions"] = ["CandidateName", "CallSign"],
    };

    public static IReadOnlyList<string> For(string key) => ByKey.TryGetValue(key, out var list) ? list : [];
}
