namespace VeSessionManager.Core.Entities;

/// <summary>
/// A per-team label for a VE — "team member", "auditioning", "session manager", "team lead",
/// "admin" to start with, and whatever else a team invents (issue #142).
///
/// <para><b>Tags carry no authorization whatsoever.</b> Some of the starting names deliberately
/// match real roles in this app's access model, because those are the words the team already uses —
/// but a VE tagged "admin" gets nothing from it. Tags exist for reporting and for deciding who to
/// invite to a session. Nothing in <c>Core/Authorization</c> may ever read them; a test asserts it,
/// because this is exactly the kind of promise that erodes the first time reading them would be
/// convenient. Every screen showing them says so.</para>
///
/// <para><b>No tag means guest.</b> That is derived at render time, never stored — a stored "guest"
/// tag would have to be added and removed in step with every other tag change, and would be wrong in
/// between.</para>
/// </summary>
public class VeTag
{
    public int Id { get; set; }

    /// <summary>Tags are a team's own vocabulary, not a shared list — two teams may use the same word differently, and neither should be able to rename the other's.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Display order on the VE screens, so a team can put its most-used tags first rather than living with alphabetical.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Optional <c>#RRGGBB</c> colour, used to colour-code the tag's chip and — via the
    /// highest-priority tag a membership carries — the team panel on the VE detail page.
    ///
    /// <para>Null means "no colour", which renders exactly as it did before colours existed. Never
    /// read this straight into a view: go through <c>VeTagColor.ForStyle</c>, which re-validates on
    /// the way out. The value lands in a CSS custom property, and HTML-encoding does not make a
    /// string safe in a stylesheet.</para>
    /// </summary>
    public string? Color { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<VeTagAssignment> Assignments { get; } = [];
}

/// <summary>
/// Join between a <see cref="VeTeamMembership"/> and a <see cref="VeTag"/>. On the membership rather
/// than the person so the same human can be tagged differently by each team they serve.
/// </summary>
public class VeTagAssignment
{
    public int VeTeamMembershipId { get; set; }
    public VeTeamMembership VeTeamMembership { get; set; } = null!;

    public int VeTagId { get; set; }
    public VeTag VeTag { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }
}
