using System.Security.Claims;

namespace VeSessionManager.Web;

/// <summary>
/// The authentication scheme a volunteer examiner gets after following a sign-in link (issue #142
/// phase 5). Separate from Identity on purpose — see the registration comment in Program.cs.
///
/// <para><b>A VE principal carries no role claim, and nothing here should ever add one.</b> That is
/// the last of the three independent barriers between this scheme and the admin app: even if the
/// scheme name and the cookie path both failed, every <c>[Authorize(Roles = ...)]</c> in the
/// application would still refuse it.</para>
/// </summary>
public static class VeSelfServiceAuth
{
    public const string Scheme = "VeSelfService";

    /// <summary>Deliberately not <see cref="ClaimTypes.NameIdentifier"/>: that is what Identity uses for a User id, and a VE id is not a User id. Keeping them in different claim types means a mix-up cannot silently resolve to the wrong record.</summary>
    public const string VolunteerExaminerIdClaim = "vesm:ve-id";

    public static ClaimsPrincipal BuildPrincipal(int volunteerExaminerId, string name) =>
        new(new ClaimsIdentity(
            [
                new Claim(VolunteerExaminerIdClaim, volunteerExaminerId.ToString()),
                new Claim(ClaimTypes.Name, name)
            ],
            Scheme));

    /// <summary>The signed-in VE's id, or null when the principal is anything else — including a perfectly valid admin.</summary>
    public static int? GetVolunteerExaminerId(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst(VolunteerExaminerIdClaim);
        return claim is not null && int.TryParse(claim.Value, out var id) ? id : null;
    }
}
