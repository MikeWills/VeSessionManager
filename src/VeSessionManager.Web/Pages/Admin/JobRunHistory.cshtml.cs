using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: read-only job run history / ops dashboard, scoped via AdminAccessScope.ScopeJobRunHistory (SystemAdmin: every run including global jobs; TeamAdmin: their own team's per-team runs only).</summary>
[Authorize(Roles = RoleGroups.Admins)]
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
        var runs = await scoped.OrderByDescending(j => j.StartedUtc).Take(200).ToListAsync(HttpContext.RequestAborted);

        Runs = runs.Select(j => new JobRunRow(j.JobName, j.Team?.Name, j.StartedUtc, j.CompletedUtc, j.Success, j.ErrorMessage, j.ResultSummary, j.IsRunning, j.StatusText)).ToList();
        return Page();
    }

    /// <summary>
    /// CSV of the same runs the page shows, for handing to someone who cannot reach the server —
    /// the beta box sits behind Tailscale, so its Worker log is not casually readable, and this is
    /// the fastest way to get a run history into a bug report.
    ///
    /// <para>Exports more rows than the page renders: on screen 200 is plenty to scan, but a support
    /// dump wants the surrounding history, and the file is a few tens of KB either way.</para>
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Scoped identically to the page — a TeamAdmin must not export runs they cannot see.
        var scoped = adminAccessScope.ScopeJobRunHistory(dbContext.JobRunHistories.Include(j => j.Team), user);
        var runs = await scoped.OrderByDescending(j => j.StartedUtc).Take(ExportRowLimit).ToListAsync(HttpContext.RequestAborted);

        var csv = new StringBuilder();
        csv.AppendLine("StartedUtc,CompletedUtc,DurationSeconds,Job,Team,Status,Result,Error");
        foreach (var run in runs)
        {
            var duration = run.CompletedUtc is { } done
                ? (done - run.StartedUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                : "";

            csv.AppendLine(string.Join(",",
                CsvExport.Field(run.StartedUtc.ToString("o", CultureInfo.InvariantCulture)),
                CsvExport.Field(run.CompletedUtc?.ToString("o", CultureInfo.InvariantCulture)),
                CsvExport.Field(duration),
                CsvExport.Field(run.JobName),
                CsvExport.Field(run.Team?.Name ?? "(global)"),
                CsvExport.Field(run.StatusText),
                CsvExport.Field(run.ResultSummary),
                CsvExport.Field(run.ErrorMessage)));
        }

        var bytes = CsvExport.ToBytes(csv);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "text/csv", $"job-run-history-{stamp}.csv");
    }

    /// <summary>Rows in the export. Deliberately larger than the 200 the page renders — see OnGetExportAsync.</summary>
    private const int ExportRowLimit = 2000;

    /// <param name="IsRunning">Computed on <see cref="JobRunHistory"/>, carried here so the view and the CSV agree.</param>
    public record JobRunRow(string JobName, string? TeamName, DateTime StartedUtc, DateTime? CompletedUtc, bool Success, string? ErrorMessage, string? ResultSummary, bool IsRunning, string StatusText);
}
