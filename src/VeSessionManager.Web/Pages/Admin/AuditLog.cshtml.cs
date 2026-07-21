using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: read-only audit log viewer, scoped via AdminAccessScope.ScopeAuditLog (SystemAdmin: global; TeamAdmin: their own team's users' actions only — see that method's doc for the known "misses unattributed background-job entries" limitation).</summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class AuditLogModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope) : PageModel
{
    public IReadOnlyList<AuditLogRow> Entries { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var scoped = adminAccessScope.ScopeAuditLog(dbContext.AuditLogs.Include(a => a.User), user);
        var entries = await scoped.OrderByDescending(a => a.TimestampUtc).Take(200).ToListAsync();

        Entries = entries.Select(a => new AuditLogRow(a.TimestampUtc, a.User?.Name, a.Action, a.EntityType, a.EntityId, a.Details)).ToList();
        return Page();
    }

    public record AuditLogRow(DateTime TimestampUtc, string? UserName, string Action, string EntityType, int EntityId, string? Details);
}
