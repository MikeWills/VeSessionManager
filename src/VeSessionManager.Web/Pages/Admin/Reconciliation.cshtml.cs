using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Where ExamTools and this app disagree — see docs/reconciliation.md.
///
/// <para><b>This page is the point of the whole feature.</b> The sweep could have stopped at a Job
/// History line reading "3 sessions missing", but a count inside a sentence, on a green row, on a
/// page nobody opens unless something is already known to be wrong, is not a report — it is a
/// record. This app learned that when the Worker printed <c>sent 0, failed 1</c> for a day while
/// the dashboard showed success.</para>
///
/// <para><b>Each finding carries its own fix.</b> Detection was the hard part of the bug that
/// prompted this; the repair was a single re-import, and the only real work was translating "the
/// 31st of May is missing" into a date range. So the row does that translation and offers the
/// button.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class ReconciliationModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    HistoricalImportService historicalImportService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public bool IncludeResolved { get; set; }

    public IReadOnlyList<FindingRow> Findings { get; private set; } = [];
    public int OpenCount { get; private set; }

    /// <param name="ImportStart">The month containing the session — re-importing the whole month is both simpler to explain and no more expensive than a single day, since the import chunks by month anyway.</param>
    public record FindingRow(
        ReconciliationFinding Finding,
        string TeamName,
        DateOnly ImportStart,
        DateOnly ImportEnd);

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        return loaded ?? Page();
    }

    /// <summary>
    /// Queues the re-import that would fix one finding. Deliberately a human action rather than
    /// something the sweep does itself: an import is real load on somebody else's API, and a job
    /// that silently starts fetching months of history because it found a discrepancy is not a
    /// monitor any more.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(int findingId)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        // Must be one this page actually listed — a posted id from another team's findings is not
        // actionable just because it exists.
        var row = Findings.FirstOrDefault(f => f.Finding.Id == findingId);
        if (row is null)
        {
            return Forbid();
        }

        var user = await CurrentUserAsync();
        var result = await historicalImportService.QueueAsync(
            row.Finding.TeamId, row.ImportStart, row.ImportEnd, user.Id, HttpContext.RequestAborted);

        TempData[result == HistoricalImportQueueResult.Queued ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            HistoricalImportQueueResult.Queued =>
                $"Re-import queued for {row.TeamName}, {row.ImportStart:MMMM yyyy}. The Worker picks it up on its next tick; the finding clears on the following sweep.",
            HistoricalImportQueueResult.AlreadyRunning => "An import is already queued or running for this team — one at a time.",
            HistoricalImportQueueResult.InvalidRange => "That date range is not valid.",
            _ => "Could not queue that import."
        };

        return RedirectToPage(new { includeResolved = IncludeResolved });
    }

    /// <summary>
    /// Plain await, not ContinueWith (L-12). The previous form was wrong in three ways at once:
    /// <c>t.Result</c> wraps anything the antecedent threw in an <see cref="AggregateException"/>,
    /// so the InvalidOperationException intended here — and any OperationCanceledException from a
    /// cancelled request — arrived as something no caller catches; the continuation ran on
    /// <c>TaskScheduler.Current</c> rather than the request context; and it issued a second full
    /// three-table user load that LoadAsync had already done on the same request.
    /// </summary>
    private async Task<User> CurrentUserAsync() =>
        await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

    private async Task<IActionResult?> LoadAsync()
    {
        // GetUserWithManagerAsync, never the bare GetUserAsync — the scope classes read
        // user.UserTeams, which the plain call leaves unloaded. See CLAUDE.md.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // null means "every team" for a SystemAdmin, which is the convention throughout — not an
        // empty set. Getting this backwards is how pages end up blank for exactly the role that
        // should see everything.
        var teamIds = accessScope.ResolveViewableTeamIds(user, null);

        var query = dbContext.ReconciliationFindings
            .Include(f => f.Team)
            .Where(f => teamIds == null || teamIds.Contains(f.TeamId));

        OpenCount = await query.CountAsync(f => f.ResolvedUtc == null, HttpContext.RequestAborted);

        if (!IncludeResolved)
        {
            query = query.Where(f => f.ResolvedUtc == null);
        }

        var findings = await query
            .OrderBy(f => f.ResolvedUtc == null ? 0 : 1)
            .ThenByDescending(f => f.SessionDateUtc)
            .Take(500)
            .ToListAsync(HttpContext.RequestAborted);

        Findings = [.. findings.Select(f =>
        {
            var date = DateOnly.FromDateTime(f.SessionDateUtc);
            var monthStart = new DateOnly(date.Year, date.Month, 1);
            var monthEnd = new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
            return new FindingRow(f, f.Team.Name, monthStart, monthEnd);
        })];

        return null;
    }
}
