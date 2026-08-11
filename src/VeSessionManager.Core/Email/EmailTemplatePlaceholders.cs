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
        ["FccFeeReminder5Day"] = ["CandidateName", "SessionDate", "Frn", "FccApplicationFileNumber"],
        ["PaymentExpirationNotice"] = ["CandidateName", "SessionDate", "PaymentAmount"],
        ["FelonyDisclosureInstructions"] = ["CandidateName"],
        ["ArrlYouthProgramInstructions"] = ["CandidateName", "CallSign"],
    };

    /// <summary>
    /// Tokens available in <b>every</b> template regardless of Key, because they are substituted by
    /// EmailTemplateRenderer itself from team-level data rather than supplied per send by
    /// CandidateNotificationService/PaymentReminderService.
    ///
    /// <para>Deliberately separate from <see cref="ByKey"/> rather than merged into it: ByKey means
    /// "what the calling service passes in", and EmailTemplatePlaceholdersTests asserts its exact
    /// contents to catch drift against that code. Folding a renderer-provided token in would break
    /// that invariant and make the drift test meaningless.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Universal = ["Logo"];

    public static IReadOnlyList<string> For(string key) => ByKey.TryGetValue(key, out var list) ? list : [];

    /// <summary>What the Email Templates admin page offers as insertable chips — the caller-provided tokens for this Key plus the universal ones.</summary>
    public static IReadOnlyList<string> ForEditor(string key) => [.. For(key), .. Universal];
}
