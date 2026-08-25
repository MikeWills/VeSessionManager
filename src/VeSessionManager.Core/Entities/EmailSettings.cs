namespace VeSessionManager.Core.Entities;

/// <summary>
/// One row per Team (was a true singleton before the multi-team foundation — see
/// docs/multi-team.md) holding the Admin-configurable email settings the spec calls for — "From
/// address and Reply-To address are separately configurable... not hardcoded" — plus the public
/// privacy policy link Phase 4's RegistrationConfirmation template references ("from Phase 9" per
/// the spec, which doesn't exist yet; stored here in the meantime rather than hardcoded, so Phase
/// 9's admin UI has an obvious place to surface it later). Not explicitly named in the original
/// Shared Data Model — EmailTemplate content was, but not where the From/Reply-To/PrivacyPolicyUrl
/// values themselves live.
/// </summary>
public class EmailSettings
{
    public int Id { get; set; }

    /// <summary>Not in the original shared data model — added as part of the multi-team foundation. This row's owning Team; unique per Team (was implicitly a singleton before).</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public required string FromAddress { get; set; }
    public string? FromDisplayName { get; set; }
    public required string ReplyToAddress { get; set; }
    public required string PrivacyPolicyUrl { get; set; }

    /// <summary>Not in the original shared data model — added in Phase 6, originally as where the now-removed PaymentExpirationNotice template went ("to Mike," per the spec, not to the candidate). Still live: any MessageRule with Recipient = TeamAdminAddress resolves here (MessageDispatchService) — the Session Manager's own inbox, not a candidate-facing address. Same hand-edit-in-the-DB pattern as the other fields on this row.</summary>
    public required string AdminNotificationEmail { get; set; }

    /// <summary>
    /// Optional. When set, every <b>candidate-facing</b> email this team sends is blind-copied here,
    /// so someone can see what actually goes out rather than waiting for a candidate to report that
    /// something looked wrong (issue #207).
    ///
    /// <para><b>Never applied to password resets, VE self-service links, or email-change
    /// confirmations</b> — those carry access tokens, and a copy in a shared inbox would be an
    /// account-takeover path.</para>
    ///
    /// <para><b>Contains candidate PII, in a place the purge cannot reach.</b> A blind-copied
    /// confirmation carries a candidate's name and email, and once delivered it lives in that
    /// mailbox indefinitely — PiiPurgeService clears database columns, not mail archives. Intended
    /// as a temporary diagnostic; clear it when it has served its purpose.</para>
    /// </summary>
    public string? BccAddress { get; set; }

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
