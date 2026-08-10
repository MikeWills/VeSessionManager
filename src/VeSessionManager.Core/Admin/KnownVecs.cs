namespace VeSessionManager.Core.Admin;

/// <summary>
/// The fourteen FCC-accredited VECs and the code ExamTools reports them under, read from the
/// "From VEC" filter on <c>https://hamstudy.org/sessions</c> (issue #83, 2026-08-10) — each entry
/// there links to <c>/sessions/{code}/inperson</c>, and that slug is the same code space ExamTools
/// puts on a session's <c>vec</c> field. Three of these codes (<c>arrl</c>, <c>lagroup</c>,
/// <c>sandarc</c>) were already confirmed against live ExamTools data, which is what makes the rest
/// trustworthy rather than guessed. See docs/vec-examtools-code.md.
/// </summary>
public static class KnownVecs
{
    /// <summary>
    /// Ordered by code so the seeded rows land in a stable, reviewable order. <c>Name</c> is
    /// HamStudy's own display spelling — deliberately not "improved," so it can be diffed against
    /// the source later.
    /// </summary>
    public static IReadOnlyList<KnownVec> All { get; } =
    [
        new("anchorage", "Anchorage ARC"),
        new("arrl", "ARRL-VEC", SupportsYouthProgram: true),
        new("cavec", "CAVEC"),
        new("golden", "GEARS"),
        new("jefferson", "Jefferson ARC"),
        new("lagroup", "GLAARG"),
        new("laurel", "Laurel ARC, Inc"),
        new("mo-kan", "MO-KAN VEC"),
        new("mrac", "MRAC VEC, Inc"),
        new("sandarc", "SANDARC"),
        new("sunnyvale", "Sunnyvale VEC"),
        new("w4vec", "W4VEC"),
        new("w5yi", "W5YI"),
        new("west-carolina", "Western Carolina ARS VEC")
    ];
}

/// <param name="Code">
/// The value ExamTools puts on a session's <c>vec</c> field. Lowercase, and compared
/// case-insensitively everywhere — never displayed as the VEC's identity.
/// </param>
/// <param name="Name">The display name, which for nine of the fourteen differs from the code.</param>
/// <param name="SupportsYouthProgram">
/// Only ARRL runs the youth discount/FCC-fee-reimbursement scholarship program, so this is true for
/// exactly one row. It seeds <see cref="Entities.Vec.SupportsYouthProgram"/>, which an admin can
/// still change afterwards.
/// </param>
public record KnownVec(string Code, string Name, bool SupportsYouthProgram = false);
