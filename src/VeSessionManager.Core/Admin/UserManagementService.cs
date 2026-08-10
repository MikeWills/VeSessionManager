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
    public async Task<(UserActionResult Result, User? User)> CreateAsync(string email, string name, UserRole role, string initialPassword, int actingUserId, CancellationToken cancellationToken, string? callSign = null)
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
            CallSign = CallSign.NormalizeFormat(callSign),
            Role = role,
            // The admin picked this password, so the owner must replace it before doing anything
            // else. Cleared by ChangePasswordAsync below / the self-service page.
            MustChangePassword = true
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

    /// <summary>
    /// A signed-in user changing their own password. Verifies the current one (UserManager does it),
    /// applies the same password policy as account creation, and clears
    /// <see cref="User.MustChangePassword"/>.
    ///
    /// <para>Deliberately separate from PasswordResetService: that flow proves identity by emailing a
    /// token, and is for someone who <i>cannot</i> sign in. This one is for someone already signed in,
    /// where the current password is the proof — which matters on a deployment with no system SMTP,
    /// where the emailed route does not work at all.</para>
    /// </summary>
    public async Task<UserActionResult> ChangeOwnPasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        // An OAuth account has no local password to change; their provider owns the credential.
        if (!await userManager.HasPasswordAsync(user))
        {
            return UserActionResult.NoLocalPassword;
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            // Wrong current password and a policy violation are both surfaced as one failure to the
            // caller, which decides the wording. Nothing here distinguishes them for the user.
            return UserActionResult.InvalidPassword;
        }

        user.MustChangePassword = false;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Actor and subject are the same person, which is the point: this is the one password change
        // nobody else performed.
        AddAudit(user.Id, "UserPasswordChanged", user.Id, $"User {user.Id} changed their own password.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
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

    /// <summary>
    /// Sets (or clears) the account holder's call sign. Blank input clears rather than storing an
    /// empty string, so "no call sign" has one representation. Stored upper-invariant to match
    /// VolunteerExaminer.CallSign's existing convention.
    /// </summary>
    public async Task<UserActionResult> SetCallSignAsync(int targetUserId, string? callSign, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        user.CallSign = CallSign.NormalizeFormat(callSign);
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserCallSignChanged", user.Id,
            $"User {user.Id} call sign set to {user.CallSign ?? "(none)"}.", now);
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

        // **The manager link is a record of who a lead reports to. It grants no access.**
        //
        // It used to decide a TeamLead's team scope too, and the validation here defended that: the
        // manager had to share a team with the lead, so a TeamAdmin could not widen a lead's
        // visibility by picking an outsider. Scope now comes from the lead's own team assignment
        // (see SessionAccessScope), because a manager can belong to several teams while a lead
        // belongs to one - inheriting the manager's set handed the lead every other team that
        // manager worked on.
        //
        // With no access riding on it, the only thing left worth checking is that the person named
        // can actually manage someone. A team-overlap rule here would now block a legitimate
        // reporting line for no security benefit - which is exactly what it did (2026-08-07).
        if (managerUserId is not null)
        {
            var manager = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == managerUserId.Value, cancellationToken);
            if (manager is null || manager.Role is not (UserRole.SessionManager or UserRole.TeamAdmin))
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
    InvalidManager,

    /// <summary>The account signs in through an external provider, so there is no local password to change.</summary>
    NoLocalPassword
}
