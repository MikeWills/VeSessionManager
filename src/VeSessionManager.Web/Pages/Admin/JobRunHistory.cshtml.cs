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
        var runs = await scoped.OrderByDescending(j => j.StartedUtc).Take(ExportRowLimit).ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("StartedUtc,CompletedUtc,DurationSeconds,Job,Team,Status,Result,Error");
        foreach (var run in runs)
        {
            var duration = run.CompletedUtc is { } done
                ? (done - run.StartedUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                : "";

            csv.AppendLine(string.Join(",",
                Csv(run.StartedUtc.ToString("o", CultureInfo.InvariantCulture)),
                Csv(run.CompletedUtc?.ToString("o", CultureInfo.InvariantCulture)),
                Csv(duration),
                Csv(run.JobName),
                Csv(run.Team?.Name ?? "(global)"),
                Csv(run.Success ? "Success" : "Failed"),
                Csv(run.ResultSummary),
                Csv(run.ErrorMessage)));
        }

        // UTF-8 BOM so Excel opens it as UTF-8 rather than mangling anything non-ASCII.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "text/csv", $"job-run-history-{stamp}.csv");
    }

    /// <summary>Rows in the export. Deliberately larger than the 200 the page renders — see OnGetExportAsync.</summary>
    private const int ExportRowLimit = 2000;

    /// <summary>
    /// Quotes a CSV field, and neutralises spreadsheet formula injection.
    ///
    /// <para>Excel and Sheets evaluate a cell beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
    /// carriage return as a formula. Error messages here are exception text, which is not something
    /// this app chooses — so a leading apostrophe is prepended to force those to be read as text.
    /// Quoting alone does not prevent it: Excel strips the quotes and evaluates what is inside.</para>
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var first = value[0];
        if (first is '=' or '+' or '-' or '@' || first == (char)9 || first == (char)13)
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public record JobRunRow(string JobName, string? TeamName, DateTime StartedUtc, DateTime? CompletedUtc, bool Success, string? ErrorMessage, string? ResultSummary);
}
