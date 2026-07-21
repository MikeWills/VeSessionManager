using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Phase 9c: user create/role/manager/deactivate management. SystemAdmin sees every user with a
/// team filter; TeamAdmin sees only their own team's SessionManager/TeamLead rows (never another
/// TeamAdmin/SystemAdmin, per AdminAccessScope.CanManageUser) and can only ever grant
/// SessionManager/TeamLead (AdminAccessScope.CanAssignRole).
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class UsersModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, AdminAccessScope adminAccessScope, UserManagementService userManagementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }
    public int CurrentUserId { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<UserRole> AssignableRoles { get; private set; } = [];
    public IReadOnlyList<(int Id, string Name)> AvailableManagers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        CurrentUserId = user.Id;
        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AssignableRoles = IsSystemAdmin
            ? [UserRole.SystemAdmin, UserRole.TeamAdmin, UserRole.SessionManager, UserRole.TeamLead]
            : [UserRole.SessionManager, UserRole.TeamLead];

        AvailableTeams = IsSystemAdmin
            ? await dbContext.Teams.OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync()
            : [];

        var effectiveTeamId = IsSystemAdmin ? TeamId : accessScope.GetEffectiveTeamId(user);
        TeamId = effectiveTeamId;

        var query = dbContext.Users.Include(u => u.Team).Include(u => u.ManagedByUser).AsQueryable();
        if (!IsSystemAdmin)
        {
            if (effectiveTeamId is null)
            {
                return Page();
            }
            query = query.Where(u => u.TeamId == effectiveTeamId && (u.Role == UserRole.SessionManager || u.Role == UserRole.TeamLead));
        }
        else if (effectiveTeamId is not null)
        {
            query = query.Where(u => u.TeamId == effectiveTeamId);
        }

        var users = await query.OrderBy(u => u.Name).ToListAsync();
        Users = users.Select(u => new UserRow(
            u.Id, u.Email ?? "", u.Name, u.Role, u.Team?.Name, IsActive(u), u.ManagedByUser?.Name)).ToList();

        AvailableManagers = await dbContext.Users
            .Where(u => u.TeamId == effectiveTeamId && (u.Role == UserRole.SessionManager || u.Role == UserRole.TeamAdmin))
            .Select(u => new ValueTuple<int, string>(u.Id, u.Name))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string email, string name, UserRole role, int? teamId, string password)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !adminAccessScope.CanAssignRole(user, role))
        {
            return Forbid();
        }

        var effectiveTeamId = user.Role == UserRole.SystemAdmin ? teamId : accessScope.GetEffectiveTeamId(user);
        var (result, _) = await userManagementService.CreateAsync(email, name, role, effectiveTeamId, password, user.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            UserActionResult.Success => $"User '{email}' created.",
            UserActionResult.DuplicateEmail => $"A user with email '{email}' already exists.",
            _ => "Could not create user — check the password meets the minimum requirements."
        };
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostSetRoleAsync(int targetUserId, UserRole newRole, int? teamId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();
        if (!adminAccessScope.CanAssignRole(auth.Value.ActingUser, newRole)) return Forbid();

        var effectiveTeamId = auth.Value.ActingUser.Role == UserRole.SystemAdmin ? teamId : accessScope.GetEffectiveTeamId(auth.Value.ActingUser);
        var result = await userManagementService.SetRoleAsync(targetUserId, newRole, effectiveTeamId, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result == UserActionResult.Success ? "Role updated." : "User not found.";
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostSetManagerAsync(int targetUserId, int? managerUserId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.SetManagerAsync(targetUserId, managerUserId, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result == UserActionResult.Success ? "Manager updated." : "User not found.";
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostDeactivateAsync(int targetUserId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.DeactivateAsync(targetUserId, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            UserActionResult.Success => "User deactivated.",
            UserActionResult.CannotDeactivateSelf => "You cannot deactivate your own account.",
            _ => "User not found."
        };
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostReactivateAsync(int targetUserId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.ReactivateAsync(targetUserId, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result == UserActionResult.Success ? "User reactivated." : "User not found.";
        return RedirectToPage(new { teamId = TeamId });
    }

    private static bool IsActive(User user) => user.LockoutEnd is null || user.LockoutEnd <= DateTimeOffset.UtcNow;

    private async Task<(User ActingUser, User TargetUser)?> AuthorizeManageAsync(int targetUserId)
    {
        var actingUser = await userManager.GetUserAsync(User);
        if (actingUser is null)
        {
            return null;
        }

        var targetUser = await userManager.FindByIdAsync(targetUserId.ToString());
        if (targetUser is null || !adminAccessScope.CanManageUser(actingUser, targetUser))
        {
            return null;
        }

        return (actingUser, targetUser);
    }

    public record UserRow(int Id, string Email, string Name, UserRole Role, string? TeamName, bool IsActive, string? ManagerName);
}
