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
/// team filter; TeamAdmin sees only SessionManager/TeamLead rows sharing at least one of their own
/// teams (never another TeamAdmin/SystemAdmin, per AdminAccessScope.CanManageUser) and can only
/// ever grant SessionManager/TeamLead (AdminAccessScope.CanAssignRole). Team membership is a
/// separate action from role assignment (SetTeamsAsync) since issue #19 — a user can belong to more
/// than one team.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class UsersModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope, UserManagementService userManagementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>Label for the team-picker trigger, same shape as the session list's.</summary>
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public int CurrentUserId { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<UserRole> AssignableRoles { get; private set; } = [];
    public IReadOnlyList<(int Id, string Name)> AvailableManagers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await LoadCurrentUserAsync();
        if (user is null)
        {
            return Forbid();
        }

        CurrentUserId = user.Id;
        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AssignableRoles = IsSystemAdmin
            ? [UserRole.SystemAdmin, UserRole.TeamAdmin, UserRole.SessionManager, UserRole.TeamLead]
            : [UserRole.SessionManager, UserRole.TeamLead];

        AvailableTeams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, AvailableTeams.Select(t => t.Id).ToList());
        TeamId = effectiveTeamId;
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var query = dbContext.Users
            .Include(u => u.UserTeams).ThenInclude(ut => ut.Team)
            .Include(u => u.ManagedByUser)
            .Include(u => u.VolunteerExaminer)
            .AsQueryable();
        if (!IsSystemAdmin)
        {
            if (effectiveTeamId is null)
            {
                return Page();
            }
            query = query.Where(u => u.UserTeams.Any(ut => ut.TeamId == effectiveTeamId) && (u.Role == UserRole.SessionManager || u.Role == UserRole.TeamLead));
        }
        else if (effectiveTeamId is not null)
        {
            query = query.Where(u => u.UserTeams.Any(ut => ut.TeamId == effectiveTeamId));
        }

        var users = await query.OrderBy(u => u.Name).ToListAsync();

        // Suggestions come from the service one user at a time, rather than a bulk query here that
        // re-implements the same rules. Those rules are the careful part — a call-sign match is a
        // suggestion, not a fact, and every ambiguous case must return nothing — so they get one
        // definition. This page lists administrators, not candidates: a handful of rows, and only
        // the unlinked ones ask.
        var rows = new List<UserRow>(users.Count);
        foreach (var u in users)
        {
            var suggestion = u.VolunteerExaminerId is null
                ? await userManagementService.SuggestVolunteerExaminerAsync(u.Id, HttpContext.RequestAborted)
                : null;

            rows.Add(new UserRow(
                u.Id, u.Email ?? "", u.Name, u.CallSign, u.Role,
                string.Join(", ", u.UserTeams.Select(ut => ut.Team.Name).OrderBy(n => n)),
                u.UserTeams.Select(ut => ut.TeamId).ToList(),
                IsActive(u), u.ManagedByUser?.Name,
                LinkedVeName: u.VolunteerExaminer is { } linked ? $"{linked.CallSign ?? "?"} — {linked.Name}" : null,
                SuggestedVeId: suggestion?.Id,
                SuggestedVeName: suggestion is null ? null : $"{suggestion.CallSign ?? "?"} — {suggestion.Name}"));
        }

        Users = rows;

        // A TeamLead has no team of their own — theirs is inherited from whoever manages them
        // (SessionAccessScope.GetEffectiveTeamIds resolves it through ManagedByUser), so this list is
        // the ONLY way to move a TeamLead between teams.
        //
        // It used to filter on `ut.TeamId == effectiveTeamId` with no branch. For a SystemAdmin on
        // "All teams" that value is null, and SQL `TeamId = NULL` is never true — so the list came
        // back empty and the Assign manager dropdown offered nothing but "(none)". Changing a
        // TeamLead's team was impossible from the default view (reported 2026-08-07). Same null-
        // comparison trap CLAUDE.md records for `x.Id != someNullableInt`.
        //
        // Branched explicitly rather than made null-tolerant in one expression, so the behaviour
        // no longer depends on how a provider treats a null comparison at all.
        var managerCandidates = dbContext.Users
            .Where(u => u.Role == UserRole.SessionManager || u.Role == UserRole.TeamAdmin);

        if (effectiveTeamId is { } scopedTeamId)
        {
            managerCandidates = managerCandidates.Where(u => u.UserTeams.Any(ut => ut.TeamId == scopedTeamId));
        }
        else
        {
            // No team chosen. Show every manager the acting user is allowed to see — for a
            // SystemAdmin that is all of them, which is what makes picking an HRCC manager from the
            // unfiltered view possible.
            var visibleTeamIds = adminAccessScope.GetEffectiveTeamIds(user);
            if (visibleTeamIds is not null)
            {
                managerCandidates = managerCandidates.Where(u => u.UserTeams.Any(ut => visibleTeamIds.Contains(ut.TeamId)));
            }
        }

        AvailableManagers = (await managerCandidates
                .Select(u => new { u.Id, u.Name, Teams = u.UserTeams.Select(ut => ut.Team.Name).ToList() })
                .ToListAsync())
            .OrderBy(u => u.Name)
            // The team is the whole point of the choice when several are listed, and two managers can
            // share a name; without it you are picking blind.
            .Select(u => new ValueTuple<int, string>(
                u.Id,
                u.Teams.Count > 0 ? $"{u.Name} ({string.Join(", ", u.Teams.OrderBy(t => t))})" : u.Name))
            .ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string email, string name, UserRole role, string password, string? callSign)
    {
        var user = await LoadCurrentUserAsync();
        if (user is null || !adminAccessScope.CanAssignRole(user, role))
        {
            return Forbid();
        }

        var (result, _) = await userManagementService.CreateAsync(email, name, role, password, user.Id, CancellationToken.None, callSign);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            UserActionResult.Success => $"User '{email}' created — assign a team below to give them access.",
            UserActionResult.DuplicateEmail => $"A user with email '{email}' already exists.",
            _ => "Could not create user — check the password meets the minimum requirements."
        };
        return RedirectToPage(new { teamId = TeamId });
    }

    /// <summary>
    /// Links a login to the VE record for the same person, or clears it when volunteerExaminerId is
    /// omitted. Identity only — see User.VolunteerExaminerId; this grants nothing.
    /// </summary>
    public async Task<IActionResult> OnPostSetVolunteerExaminerAsync(int targetUserId, int? volunteerExaminerId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.SetVolunteerExaminerAsync(
            targetUserId, volunteerExaminerId, auth.Value.ActingUser.Id, CancellationToken.None);

        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            UserActionResult.Success when volunteerExaminerId is null => "VE link cleared.",
            UserActionResult.Success => "Linked to VE record.",
            UserActionResult.VolunteerExaminerAlreadyLinked => "That VE record is already linked to another user.",
            _ => "Could not update the VE link."
        };
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostSetCallSignAsync(int targetUserId, string? callSign)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.SetCallSignAsync(targetUserId, callSign, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == UserActionResult.Success ? "Call sign updated." : "Could not update call sign.";
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostSetRoleAsync(int targetUserId, UserRole newRole)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();
        if (!adminAccessScope.CanAssignRole(auth.Value.ActingUser, newRole)) return Forbid();

        var result = await userManagementService.SetRoleAsync(targetUserId, newRole, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result == UserActionResult.Success ? "Role updated." : "User not found.";
        return RedirectToPage(new { teamId = TeamId });
    }

    /// <summary>Issue #19: replaces a user's team memberships wholesale — the actual multi-team
    /// assignment mechanism. A TeamAdmin may only grant teams from their own AvailableTeams (a
    /// tampered request for a team they don't manage is silently dropped, not erred on).</summary>
    public async Task<IActionResult> OnPostSetTeamsAsync(int targetUserId, List<int> teamIds)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        // SetTeamsAsync replaces a user's team list wholesale, so simply filtering the *requested*
        // ids to a TeamAdmin's own teams isn't enough — a target user who also belongs to a team
        // this TeamAdmin doesn't manage (CanManageUser only requires sharing *one* team) would
        // silently be removed from it, since that team was never offered as a checkbox to begin
        // with. Any existing membership outside the acting user's own manageable teams is preserved
        // untouched; only the acting user's own teams are actually added/removed by this request.
        var allowedTeamIds = adminAccessScope.GetEffectiveTeamIds(auth.Value.ActingUser);
        List<int> finalTeamIds;
        if (allowedTeamIds is null)
        {
            finalTeamIds = teamIds;
        }
        else
        {
            var outsideActingUsersAuthority = auth.Value.TargetUser.UserTeams.Select(ut => ut.TeamId).Where(id => !allowedTeamIds.Contains(id));
            var requestedWithinAuthority = teamIds.Where(id => allowedTeamIds.Contains(id));
            finalTeamIds = outsideActingUsersAuthority.Concat(requestedWithinAuthority).Distinct().ToList();
        }

        var result = await userManagementService.SetTeamsAsync(targetUserId, finalTeamIds, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result == UserActionResult.Success ? "Teams updated." : "User not found.";
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostSetManagerAsync(int targetUserId, int? managerUserId)
    {
        var auth = await AuthorizeManageAsync(targetUserId);
        if (auth is null) return Forbid();

        var result = await userManagementService.SetManagerAsync(targetUserId, managerUserId, auth.Value.ActingUser.Id, CancellationToken.None);
        TempData[result == UserActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            UserActionResult.Success => "Manager updated.",
            UserActionResult.InvalidManager => "That manager doesn't share a team with this TeamLead, or doesn't hold a role that can manage one.",
            _ => "User not found."
        };
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

    /// <summary>UserManager.GetUserAsync doesn't include UserTeams (or ManagedByUser.UserTeams) —
    /// every method below needs at least the acting user's own UserTeams loaded for
    /// AdminAccessScope's Contains-based checks.</summary>
    private async Task<User?> LoadCurrentUserAsync()
    {
        var principalUser = await userManager.GetUserAsync(User);
        return principalUser is null
            ? null
            : await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == principalUser.Id);
    }

    private async Task<(User ActingUser, User TargetUser)?> AuthorizeManageAsync(int targetUserId)
    {
        var actingUser = await LoadCurrentUserAsync();
        if (actingUser is null)
        {
            return null;
        }

        var targetUser = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == targetUserId);
        if (targetUser is null || !adminAccessScope.CanManageUser(actingUser, targetUser))
        {
            return null;
        }

        return (actingUser, targetUser);
    }

    /// <param name="LinkedVeName">The VE record this login belongs to, when linked (#224). Null means unlinked.</param>
    /// <param name="SuggestedVeId">
    /// A VE record whose call sign matches this user's, offered as a one-click confirmation. Null
    /// whenever the answer is not unambiguous — no call sign, a placeholder, several matches, or the
    /// match already claimed. See UserManagementService.SuggestVolunteerExaminerAsync.
    /// </param>
    public record UserRow(int Id, string Email, string Name, string? CallSign, UserRole Role, string TeamNames, IReadOnlyList<int> TeamIds, bool IsActive, string? ManagerName,
        string? LinkedVeName, int? SuggestedVeId, string? SuggestedVeName);
}
