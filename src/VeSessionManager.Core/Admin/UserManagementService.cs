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
    public async Task<(UserActionResult Result, User? User)> CreateAsync(string email, string name, UserRole role, int? teamId, string initialPassword, int actingUserId, CancellationToken cancellationToken)
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
            Role = role,
            TeamId = teamId
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

    public async Task<UserActionResult> SetRoleAsync(int targetUserId, UserRole newRole, int? teamId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        user.Role = newRole;
        user.TeamId = teamId;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserRoleChanged", user.Id, $"User {user.Id} role set to {newRole}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> SetManagerAsync(int teamLeadUserId, int? managerUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(teamLeadUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
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
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = nameof(User),
            EntityId = entityId,
            TimestampUtc = now,
            Details = details
        });
}

public enum UserActionResult
{
    Success,
    NotFound,
    DuplicateEmail,
    InvalidPassword,
    CannotDeactivateSelf
}
