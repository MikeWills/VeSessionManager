using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Development-only seed of one test user per role (Phase 9a's "three test users, one per role"
/// deliverable, expanded to four for the 4-role model). Same "never touches a database that
/// already has this data" guard as the Worker's DevDataSeeder. UserManager (needed to hash
/// passwords) is naturally a Web-hosted service, so this seeding lives here rather than in the
/// Worker alongside DevDataSeeder — see docs/admin-auth.md.
/// </summary>
public static class DevAuthSeeder
{
    /// <summary>Development-only, documented in docs/admin-auth.md — not a real secret.</summary>
    public const string DevPassword = "Dev-Password1!";

    public static async Task SeedAsync(IServiceProvider services, ILogger logger)
    {
        var userManager = services.GetRequiredService<UserManager<User>>();
        var dbContext = services.GetRequiredService<AppDbContext>();

        // Not "any user exists" — the Worker's own DevDataSeeder already creates a "System" user
        // (for CreatedByUserId audit trails) sharing this same table, which would otherwise make
        // this guard skip before ever seeding the four role test users.
        if (await userManager.FindByEmailAsync("sessionmanager@example.com") is not null)
        {
            return;
        }

        // Seeded by the Phase6_5MultiTeamFoundation migration's InsertData, so this exists as soon
        // as migrations have run — no dependency on the Worker's own DevDataSeeder having run first.
        var team = await dbContext.Teams.FirstOrDefaultAsync();
        if (team is null)
        {
            logger.LogWarning("No Team row exists yet — skipping Phase 9a dev test user seeding");
            return;
        }

        var sessionManager = await CreateUserAsync(userManager, "sessionmanager@example.com", "Session Manager", UserRole.SessionManager, team.Id);
        await CreateUserAsync(userManager, "sysadmin@example.com", "System Admin", UserRole.SystemAdmin, teamId: null);
        await CreateUserAsync(userManager, "teamadmin@example.com", "Team Admin", UserRole.TeamAdmin, team.Id);
        var teamLead = await CreateUserAsync(userManager, "teamlead@example.com", "Team Lead", UserRole.TeamLead, team.Id);

        teamLead.ManagedByUserId = sessionManager.Id;
        await userManager.UpdateAsync(teamLead);

        logger.LogInformation("Seeded four Phase 9a dev test users (sysadmin/teamadmin/sessionmanager/teamlead@example.com) — see docs/admin-auth.md for the shared dev password");
    }

    private static async Task<User> CreateUserAsync(UserManager<User> userManager, string email, string name, UserRole role, int? teamId)
    {
        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Name = name,
            Role = role,
            TeamId = teamId
        };

        var result = await userManager.CreateAsync(user, DevPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Failed to seed dev user {email}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        return user;
    }
}
