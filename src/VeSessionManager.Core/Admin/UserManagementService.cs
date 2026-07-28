using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: user create/role/manager/deactivate management. Deactivation reuses ASP.NET Core
/// Identity's own LockoutEnd/LockoutEnabled columns (no new schema) — SignInManager already
/// enforces lockout on both the password and external-login sign-in paths (PreSignInCheck calls
/// IsLockedOutAsync before validating credentials), so setting LockoutEnd = MaxValue is sufficient
/// on its own. No invite/reset-email flow exists in this app — an admin creating a user sets and
/// communicates the initial password out-of-band, same spirit as DevAuthSeeder's shared dev
/// password; that's an explicit scope cut for this phase, not an oversight.
/// </summary>
public class UserManagementService(UserManager<User> userManager, AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>A brand-new user starts with zero team memberships — team assignment is now a
    /// separate action (SetTeamsAsync), same as role assignment already was, since a user can belong
    /// to more than one team (issue #19).</summary>
    public async Task<(UserActionResult Result, User? User)> CreateAsync(string email, string name, UserRole role, string initialPassword, int actingUserId, CancellationToken cancellationToken)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return (UserActionResult.DuplicateEmail, null);
        }

        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Name = name,
            Role = role
        };

        var result = await userManager.CreateAsync(user, initialPassword);
        if (!result.Succeeded)
        {
            return (UserActionResult.InvalidPassword, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserCreated", user.Id, $"User '{email}' created with role {role}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (UserActionResult.Success, user);
    }

    public async Task<UserActionResult> SetRoleAsync(int targetUserId, UserRole newRole, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        user.Role = newRole;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserRoleChanged", user.Id, $"User {user.Id} role set to {newRole}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>Replaces a TeamAdmin/SessionManager's team memberships wholesale — the actual
    /// mechanism behind issue #19 (a Session Manager can belong to multiple teams). Diffs the
    /// requested set against what's currently stored so unaffected rows are left untouched.</summary>
    public async Task<UserActionResult> SetTeamsAsync(int targetUserId, IReadOnlyList<int> teamIds, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        var requestedIds = teamIds.Distinct().ToHashSet();
        var currentIds = user.UserTeams.Select(ut => ut.TeamId).ToHashSet();

        foreach (var toRemove in user.UserTeams.Where(ut => !requestedIds.Contains(ut.TeamId)).ToList())
        {
            user.UserTeams.Remove(toRemove);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var toAdd in requestedIds.Where(id => !currentIds.Contains(id)))
        {
            user.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = toAdd, CreatedUtc = now });
        }

        AddAudit(actingUserId, "UserTeamsChanged", user.Id, $"User {user.Id}'s teams set to [{string.Join(", ", requestedIds.OrderBy(id => id))}].", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> SetManagerAsync(int teamLeadUserId, int? managerUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == teamLeadUserId, cancellationToken);
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        // A manager must actually share at least one team with the TeamLead being assigned, and
        // hold a role that's allowed to manage anyone at all — otherwise
        // SessionAccessScope.GetEffectiveTeamIds (which resolves a TeamLead's teams via
        // ManagedByUser.UserTeams) would grant them cross-team session/candidate visibility just by
        // a TeamAdmin picking a manager with no shared team.
        if (managerUserId is not null)
        {
            var manager = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == managerUserId.Value, cancellationToken);
            var sharesATeam = manager is not null && manager.UserTeams.Select(ut => ut.TeamId).Intersect(user.UserTeams.Select(ut => ut.TeamId)).Any();
            if (manager is null || !sharesATeam || manager.Role is not (UserRole.SessionManager or UserRole.TeamAdmin))
            {
                return UserActionResult.InvalidManager;
            }
        }

        user.ManagedByUserId = managerUserId;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserManagerAssigned", user.Id, $"User {user.Id}'s manager set to {(managerUserId?.ToString() ?? "none")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> DeactivateAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        if (targetUserId == actingUserId)
        {
            return UserActionResult.CannotDeactivateSelf;
        }

        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        // Lockout alone only blocks *future* sign-ins (SignInManager's PreSignInCheck) — it does
        // nothing about a cookie this user already holds. Bumping the security stamp invalidates
        // that existing cookie on its next SecurityStampValidator check (default ~30 min), so
        // deactivation actually revokes access instead of just blocking the next login attempt.
        await userManager.UpdateSecurityStampAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserDeactivated", user.Id, $"User {user.Id} deactivated.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> ReactivateAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        await userManager.SetLockoutEndDateAsync(user, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserReactivated", user.Id, $"User {user.Id} reactivated.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(User), entityId, details, now);
}

public enum UserActionResult
{
    Success,
    NotFound,
    DuplicateEmail,
    InvalidPassword,
    CannotDeactivateSelf,
    InvalidManager
}
