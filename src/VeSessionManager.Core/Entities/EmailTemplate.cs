namespace VeSessionManager.Core.Entities;

public class EmailTemplate
{
    public int Id { get; set; }

    /// <summary>Not in the original shared data model — added as part of the multi-team foundation. Template content is per-team customizable (confirmed with the user) — Key's uniqueness is now scoped to (TeamId, Key), not global.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>
    /// Identifies which automated/triggerable email this is, e.g. RegistrationConfirmation,
    /// DayBeforeReminder.
    ///
    /// <para>For a <see cref="IsUserDefined"/> template this is generated rather than meaningful:
    /// <c>Custom.&lt;slug&gt;</c>, from the name that was typed. <b>The dot is what keeps the two
    /// populations apart</b> — no shipped key contains one, so a team can never type a name that
    /// collides with a key the code looks up, including a key added years from now. What a person
    /// reads is <see cref="DisplayName"/>.</para>
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// True for a template a team wrote for itself (#144). The distinction is load-bearing rather
    /// than cosmetic: a system template is looked up by <c>Key</c> from sending code, so deleting or
    /// renaming one breaks a send that has no other way to find it. Only user-defined rows can be
    /// renamed or deleted, and that is enforced in <c>EmailTemplateAdminService</c>, not in the UI.
    /// </summary>
    public bool IsUserDefined { get; set; }

    /// <summary>
    /// The human name, for user-defined templates. Null for the shipped ones, whose label comes from
    /// <c>EmailTemplateLabels</c> — so a shipped template's name stays in one place rather than being
    /// copied into every team's row at seed time and drifting per deployment.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Who a user-defined template is written to (#191), which decides both the placeholders it can
    /// use and which compose screen offers it.
    ///
    /// <para><b>It cannot be inferred, and getting it wrong is visible to the recipient.</b> The two
    /// audiences have different tokens — a candidate template's <c>{{CandidateFirstName}}</c> resolves
    /// to nothing for a VE, and the renderer deliberately leaves an unknown token as literal
    /// <c>{{CandidateFirstName}}</c> text rather than a silent blank. So it is asked once, at
    /// creation, instead of guessed from the body.</para>
    ///
    /// <para>Meaningless for a shipped template, which is why the default is
    /// <see cref="EmailTemplateAudience.Candidates"/>: every one of them is candidate-facing, and the
    /// existing rows keep that value on migration.</para>
    /// </summary>
    public EmailTemplateAudience Audience { get; set; } = EmailTemplateAudience.Candidates;

    public required string Subject { get; set; }

    /// <summary>Plain text/HTML with {{PlaceholderKeyword}} tokens, substituted at send time.</summary>
    public required string Body { get; set; }

    // Null until an Admin edits the seeded default content.
    public int? UpdatedByUserId { get; set; }

    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// <summary>
/// Who a template is written to (#191).
///
/// <para><b>Persisted as an integer, so these values are pinned and must keep their numbers</b> — the
/// rule stated in <c>Enums.cs</c>. Append new members, never insert: renumbering would silently
/// re-point every existing template at a different audience, which reads as a template that has
/// vanished from one picker and appeared in another.</para>
/// </summary>
public enum EmailTemplateAudience
{
    /// <summary>People sitting exams. Every shipped template is this, which is why it is the default and what existing rows keep.</summary>
    Candidates = 0,

    /// <summary>The team's volunteer examiners.</summary>
    VolunteerExaminers = 1
}
