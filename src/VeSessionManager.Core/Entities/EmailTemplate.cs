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

    public required string Subject { get; set; }

    /// <summary>Plain text/HTML with {{PlaceholderKeyword}} tokens, substituted at send time.</summary>
    public required string Body { get; set; }

    // Null until an Admin edits the seeded default content.
    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
