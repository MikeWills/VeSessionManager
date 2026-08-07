namespace VeSessionManager.Core.Entities;

/// <summary>
/// A call sign a VE used to hold. Written when <see cref="VolunteerExaminer.CallSign"/> is replaced,
/// which happens when FCC reports a different call sign for a person we can identify by FRN — a
/// vanity call coming through, most often.
///
/// <para><b>This is what stops a rename from creating a second person.</b> ExamTools' session roster
/// keeps reporting whatever call sign it holds, which can be the old one for a while, and a past
/// session legitimately recorded the old call because that is who worked it. The sync therefore
/// matches FRN, then current call sign, then this table, and only creates a new
/// <see cref="VolunteerExaminer"/> when all three miss.</para>
///
/// <para>Also the honest record of a historical roster: it can say a session was worked by someone
/// who was N0ABC at the time and is W1XYZ now, which a single mutable column cannot.</para>
/// </summary>
public class VeCallSignHistory
{
    public int Id { get; set; }

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;

    /// <summary>Upper-invariant, same convention as the live column.</summary>
    public required string CallSign { get; set; }

    /// <summary>When this app first saw the VE holding it. Best-effort — for a row that predates this table it is the moment of the migration, not the FCC grant date.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>When it stopped being their current call sign, i.e. when the replacement was observed.</summary>
    public DateTime ReplacedUtc { get; set; }
}
