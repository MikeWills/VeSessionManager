namespace VeSessionManager.Web;

/// <summary>
/// A "back to the list this page belongs to" link, rendered by <c>Pages/Shared/_ParentCrumb.cshtml</c>
/// in place of a page's eyebrow text.
///
/// <para><b>The link is conditional on being able to reach the parent, which is not the same as being
/// able to reach this page.</b> The two parents do not admit the same roles — Teams takes SystemAdmin
/// and TeamAdmin, VECs is SystemAdmin only — so a crumb rendered unconditionally would hand some
/// viewers a link straight to a 403. When the parent is out of reach the eyebrow renders exactly as it
/// always did, and the page is simply unchanged for that role.</para>
/// </summary>
/// <param name="Page">Razor page path of the parent list, e.g. <c>./Teams</c>.</param>
/// <param name="Label">What the parent is called, e.g. <c>Teams</c>.</param>
/// <param name="Eyebrow">The original eyebrow text, shown to anyone who cannot reach the parent.</param>
/// <param name="ParentRoles">
/// Roles allowed on the <i>parent</i> page, comma-separated — must mirror that page's own
/// <c>[Authorize]</c>. Per-parent rather than a single blanket check because the two parents differ:
/// Teams admits SystemAdmin and TeamAdmin, VECs is SystemAdmin only.
/// </param>
public sealed record ParentCrumb(string Page, string Label, string Eyebrow, string ParentRoles)
{
    public bool IsReachableBy(System.Security.Claims.ClaimsPrincipal user) =>
        ParentRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(user.IsInRole);
}
