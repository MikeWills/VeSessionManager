namespace VeSessionManager.Core.Entities;

/// <summary>
/// A VE's pending request to change the email address their team holds — and, more importantly, the
/// address every future self-service sign-in link is sent to (issue #142 phase 5).
///
/// <para><b>Confirmed from the OLD address, which is the whole point.</b> Without that, one leaked
/// sign-in link is permanent account takeover: whoever holds it changes the address to their own and
/// every subsequent link goes to them. Requiring the current mailbox to approve means a stolen link
/// grants at most one session, never control.</para>
///
/// <para>The confirmation email <b>names the new address</b>. Old-address approval authorises the
/// change; showing the address is what catches a typo — a mistyped address would otherwise send every
/// future link somewhere the VE cannot read, recoverable only by asking an admin.</para>
///
/// <para>Same storage rules as <see cref="VeSelfServiceToken"/>: hash only, single use, short life.</para>
/// </summary>
public class VeEmailChangeRequest
{
    public int Id { get; set; }

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;

    /// <summary>What they want it changed to. Not applied until the confirmation link is followed.</summary>
    public required string NewEmail { get; set; }

    /// <summary>The address the confirmation was sent to — their address at the time of the request. Kept so the audit trail can show who actually approved it.</summary>
    public required string ConfirmationSentToEmail { get; set; }

    /// <summary>SHA-256 of the raw token, hex-encoded.</summary>
    public required string TokenHash { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConfirmedUtc { get; set; }
}
