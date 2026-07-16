namespace VeSessionManager.Core.Entities;

public class EmailTemplate
{
    public int Id { get; set; }

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
