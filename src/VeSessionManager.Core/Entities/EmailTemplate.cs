namespace VeSessionManager.Core.Entities;

public class EmailTemplate
{
    public int Id { get; set; }

    /// <summary>Not in the original shared data model — added as part of the multi-team foundation. Template content is per-team customizable (confirmed with the user) — Key's uniqueness is now scoped to (TeamId, Key), not global.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>Identifies which automated/triggerable email this is, e.g. RegistrationConfirmation, DayBeforeReminder.</summary>
    public required string Key { get; set; }

    public required string Subject { get; set; }

    /// <summary>Plain text/HTML with {{PlaceholderKeyword}} tokens, substituted at send time.</summary>
    public required string Body { get; set; }

    // Null until an Admin edits the seeded default content.
    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
