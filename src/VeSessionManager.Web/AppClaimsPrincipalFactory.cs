using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Adds the Role claim Phase 9a's authorization relies on at sign-in time, so downstream
/// role-based [Authorize] checks read straight from the signed-in cookie's claims instead of
/// hitting the DB on every request. Added under the standard ClaimTypes.Role so ASP.NET Core's
/// built-in [Authorize(Roles = "...")] works with no extra wiring.
///
/// No longer adds a "TeamId" claim (removed for issue #19/multi-team) — a user can now belong to
/// more than one Team (User.UserTeams), which a single-value claim can't represent, and nothing in
/// the authorization path actually read it: every real check (SessionAccessScope/AdminAccessScope)
/// re-fetches User (with UserTeams included) from the DB rather than trusting the cookie for that.
/// </summary>
public class AppClaimsPrincipalFactory(UserManager<User> userManager, IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<User>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        return identity;
    }
}
