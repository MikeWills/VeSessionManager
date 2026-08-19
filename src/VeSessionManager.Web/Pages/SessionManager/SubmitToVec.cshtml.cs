using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// The ARRL submission preview (issue #197) — the complete form, filled in, before anything is sent.
///
/// <para><b>This page has no POST handler, and that is the whole design of this increment.</b> There
/// is no sandbox on ARRL's side: every exercise of the real path files a real session with a real
/// VEC. So the preview ships first and on its own, exactly as the issue's rollout note asks —
/// generate it, compare it against what would be filed by hand, and keep filing by hand. The
/// submission arrives in the next PR, behind an explicit confirmation on this same screen.</para>
///
/// <para>Deliberately not a disabled submit button behind a flag: a flag is something somebody can
/// switch on early. An absent handler is not.</para>
///
/// <para>Reached from session detail as a <b>GET</b> for an ARRL session, replacing the POST toggle
/// that still serves every other VEC — so the only request that will ever reach ARRL lives here,
/// behind a page a human has read.</para>
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Authorize(Roles = RoleGroups.AllRoles)]
public class SubmitToVecModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    ArrlSubmissionPreviewService previewService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public ArrlSubmissionPreview? Preview { get; private set; }

    /// <summary>False for a TeamLead, who may read this page but takes no action on it — same split as session detail.</summary>
    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // GetUserWithManagerAsync, never the bare GetUserAsync: the scope classes read user.UserTeams,
        // which the bare load leaves empty — see CLAUDE.md's Known Constraints.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == Id);
        if (session is null || !accessScope.CanView(user, session))
        {
            return NotFound();
        }

        CanEdit = accessScope.CanEdit(user, session);
        Preview = await previewService.BuildAsync(Id, HttpContext.RequestAborted);
        return Page();
    }
}
