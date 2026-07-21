using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Adds the Role/TeamId claims Phase 9a's authorization relies on at sign-in time, so downstream
/// checks (role-based [Authorize], SessionAccessScope once it's wired into a real page in 9b) read
/// straight from the signed-in cookie's claims instead of hitting the DB on every request. Role is
/// added under the standard ClaimTypes.Role so ASP.NET Core's built-in [Authorize(Roles = "...")]
/// works with no extra wiring.
/// </summary>
public class AppClaimsPrincipalFactory(UserManager<User> userManager, IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<User>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        if (user.TeamId is not null)
        {
            identity.AddClaim(new Claim("TeamId", user.TeamId.Value.ToString()));
        }

        return identity;
    }
}
