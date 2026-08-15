namespace VeSessionManager.Web;

/// <summary>
/// The team-filter dropdown, as data (#306). Thirteen pages rendered this markup, in five variants
/// that differed only in the three knobs below — and three of them already carried a comment saying
/// "same team-picker component as the session list", so the intent was documented long before the
/// partial existed.
///
/// <para><b>The partial does not decide whether to render.</b> Each page keeps its own surrounding
/// condition — most check <c>AvailableTeams.Count &gt; 1</c>, the admin pages check
/// <c>IsSystemAdmin</c> instead — so extracting the markup changes no page's visibility rules.</para>
/// </summary>
/// <param name="SelectedTeamId">
/// Null means "All teams" where that is offered, and "nothing chosen yet" where it is not. Also
/// drives the trigger's <c>active</c> styling.
/// </param>
/// <param name="Label">The trigger text — "All teams", a team name, or a prompt like "Select a team…".</param>
/// <param name="IncludeAllTeams">
/// False on pages that edit or act on exactly one team, where a merged view has no meaning. That is
/// a real difference in behavior, not styling: without it there is no way to express "no filter".
/// </param>
/// <param name="Counts">
/// Optional per-team badge, used by the two worklists (Applicant Status, Unmatched Payments) that
/// answer "how much is outstanding for each team" before you pick one. Read through
/// <c>TeamCountExtensions.CountFor</c>, since a team with nothing outstanding is absent from the
/// dictionary rather than present as zero.
/// </param>
public sealed record TeamPicker(
    int? SelectedTeamId,
    IReadOnlyList<(int Id, string Name)> Teams,
    string Label,
    bool IncludeAllTeams = true,
    IReadOnlyDictionary<int, int>? Counts = null);
