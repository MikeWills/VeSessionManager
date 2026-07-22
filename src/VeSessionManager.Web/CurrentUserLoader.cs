using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// UserManager.GetUserAsync(ClaimsPrincipal) does a plain lookup with no eager-loading, so
/// User.ManagedByUser — the navigation SessionAccessScope.GetEffectiveTeamId requires be loaded for
/// a TeamLead (see that class's own doc comment) — comes back null even when ManagedByUserId is
/// set. Every page that resolves a TeamLead's effective team must load through here instead of
/// userManager.GetUserAsync directly, or a TeamLead silently sees zero sessions instead of their
/// assigned team's.
/// </summary>
public static class CurrentUserLoader
{
    public static async Task<User?> GetUserWithManagerAsync(this UserManager<User> userManager, AppDbContext dbContext, ClaimsPrincipal principal)
    {
        var id = userManager.GetUserId(principal);
        if (id is null || !int.TryParse(id, out var userId))
        {
            return null;
        }

        return await dbContext.Users.Include(u => u.ManagedByUser).FirstOrDefaultAsync(u => u.Id == userId);
    }
}
