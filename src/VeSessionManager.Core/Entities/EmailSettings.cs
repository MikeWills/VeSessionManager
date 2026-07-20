namespace VeSessionManager.Core.Entities;

/// <summary>
/// Singleton row (always Id = 1) holding the Admin-configurable email settings the spec calls
/// for — "From address and Reply-To address are separately configurable... not hardcoded" —
/// plus the public privacy policy link Phase 4's RegistrationConfirmation template references
/// ("from Phase 9" per the spec, which doesn't exist yet; stored here in the meantime rather than
/// hardcoded, so Phase 9's admin UI has an obvious place to surface it later). Not explicitly
/// named in the original Shared Data Model — EmailTemplate content was, but not where the
/// From/Reply-To/PrivacyPolicyUrl values themselves live.
/// </summary>
public class EmailSettings
{
    public int Id { get; set; }

    public required string FromAddress { get; set; }
    public string? FromDisplayName { get; set; }
    public required string ReplyToAddress { get; set; }
    public required string PrivacyPolicyUrl { get; set; }

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
