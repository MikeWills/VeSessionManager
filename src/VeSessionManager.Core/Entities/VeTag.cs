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

    /// <summary>
    /// The Discord role that means this tag, or null for a tag Discord has no opinion about (#519).
    ///
    /// <para><b>Only mapped tags are ever synced.</b> Null here is what keeps a hand-managed tag
    /// hand-managed: the sync neither adds nor removes it, whatever roles a matched VE holds. For a
    /// mapped tag the rule is symmetric — holding the role means holding the tag, and not holding the
    /// role means not holding it — so mapping a tag hands it to Discord in both directions.</para>
    ///
    /// <para>Unique per team (see <c>VeTagConfiguration</c>): one role means one tag. Two tags on one
    /// role is well defined to <i>run</i> ("both apply") and impossible to <i>read</i> off this
    /// screen, which is the wrong trade for a mapping an admin has to trust.</para>
    ///
    /// <para><b>Grants nothing.</b> A Discord role is not an authorization signal here any more than
    /// the tag it sets is — see the class remarks. Nor does this app ever write back: Discord roles
    /// and permissions are managed in Discord, and this is a read.</para>
    /// </summary>
    public ulong? DiscordRoleId { get; set; }

    /// <summary>
    /// What that role was called when it was last mapped or confirmed. A snapshot for display, never
    /// the link — roles get renamed, and <see cref="DiscordRoleId"/> survives it.
    ///
    /// <para>Stored rather than fetched so the tag screen can still say which role a tag is mapped to
    /// when Discord is unreachable, the bot token is unset, or the privileged intent has been turned
    /// off — all of which leave the role list empty and would otherwise render the mapping as a bare
    /// 18-digit number. Same snapshot-on-the-record reasoning as <c>Payment.CandidateNameSnapshot</c>
    /// and <c>MessageRuleRun</c>.</para>
    /// </summary>
    public string? DiscordRoleName { get; set; }

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
