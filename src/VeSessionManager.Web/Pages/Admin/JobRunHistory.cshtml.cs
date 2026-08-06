using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: read-only job run history / ops dashboard, scoped via AdminAccessScope.ScopeJobRunHistory (SystemAdmin: every run including global jobs; TeamAdmin: their own team's per-team runs only).</summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class JobRunHistoryModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope) : PageModel
{
    public IReadOnlyList<JobRunRow> Runs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var scoped = adminAccessScope.ScopeJobRunHistory(dbContext.JobRunHistories.Include(j => j.Team), user);
        var runs = await scoped.OrderByDescending(j => j.StartedUtc).Take(200).ToListAsync();

        Runs = runs.Select(j => new JobRunRow(j.JobName, j.Team?.Name, j.StartedUtc, j.CompletedUtc, j.Success, j.ErrorMessage, j.ResultSummary)).ToList();
        return Page();
    }

    public record JobRunRow(string JobName, string? TeamName, DateTime StartedUtc, DateTime? CompletedUtc, bool Success, string? ErrorMessage, string? ResultSummary);
}
