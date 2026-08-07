namespace VeSessionManager.Core.Entities;

/// <summary>
/// One VE's accreditation with one VEC (issue #142). On the person, not the membership: an
/// accreditation is issued by the VEC to the individual and travels with them to every team they
/// serve.
///
/// <para>Together with the cached license state on <see cref="VolunteerExaminer"/> this is what
/// finally answers <i>"can this person legally serve at Saturday's session?"</i> — a VE needs a
/// current license of General or higher <b>and</b> accreditation with that session's VEC. Issue #107
/// could only ever answer the license half on its own, which is why the two features landed
/// together.</para>
///
/// <para><b>Hand-entered.</b> No VEC exposes an accreditation API to this app, so nothing verifies
/// these rows — anywhere the "can they serve?" answer is shown has to be honest that half of it is
/// someone's data entry rather than a live check.</para>
/// </summary>
public class VeVecAccreditation
{
    public int Id { get; set; }

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;

    public int VecId { get; set; }
    public Vec Vec { get; set; } = null!;

    /// <summary>The VEC's own identifier for this accreditation, when the team records one. Optional — not every VEC issues a number a VE keeps to hand.</summary>
    public string? AccreditationNumber { get; set; }

    /// <summary>Optional: some VECs re-accredit on a cycle, others do not. Null means "no expiry recorded", which must not be shown as expired.</summary>
    public DateTime? ExpiresUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}
