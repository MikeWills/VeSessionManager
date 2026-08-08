namespace VeSessionManager.Core.Entities;

/// <summary>
/// A single-use, short-lived sign-in link for a VE to maintain their own contact details without an
/// account (issue #142). "Lazy login to their email", in the issue's words.
///
/// <para><b>Only the hash is stored.</b> A leaked database backup then yields nothing usable: the
/// raw token exists in the email and in memory during the request, never at rest. Same reasoning as
/// any password store, and it matters more here than for a password reset because this link opens a
/// page showing a home address.</para>
///
/// <para><b>Single use and short-lived.</b> An emailed link outlives the email — it sits in an inbox,
/// in a mail client's cache, in whatever forwarded it. Consuming it on first use means a link found
/// later is inert, and the expiry bounds the window even for one never clicked.</para>
///
/// <para>Deliberately not an ASP.NET Identity user. A VE is a person on a roster, not an account —
/// giving every VE an Identity row to edit their phone number would put them in the same table as
/// SystemAdmins and make the role model answer questions it should not have to.</para>
/// </summary>
public class VeSelfServiceToken
{
    public int Id { get; set; }

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;

    /// <summary>SHA-256 of the raw token, hex-encoded. Indexed, because a lookup by hash is how a presented token is resolved.</summary>
    public required string TokenHash { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Set the moment the link is used. A non-null value makes the token inert regardless of its expiry.</summary>
    public DateTime? ConsumedUtc { get; set; }

    /// <summary>
    /// The address the link was sent to, captured at issue time. Kept because the whole point of the
    /// token is that it proves control of <i>that</i> mailbox — if the VE's email is later changed by
    /// an admin, an outstanding link should be traceable to where it actually went.
    /// </summary>
    public required string SentToEmail { get; set; }
}
