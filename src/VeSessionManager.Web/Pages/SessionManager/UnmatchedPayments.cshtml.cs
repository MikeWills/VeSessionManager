using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Square payments that arrived (payment.updated/COMPLETED) but couldn't be matched to a Payment
/// row this app generated a link for — typically taken through a separate online payment page —
/// nor auto-matched by buyer email against exactly one candidate with an outstanding Unpaid
/// payment. See SquarePaymentMatchingService.HandleUnmatchedOrderAsync. A Session Manager resolves
/// each one here by picking the right candidate; the "match candidate" dropdown is scoped to
/// candidates who currently have an outstanding Unpaid payment (matches the same "who could
/// plausibly owe this" eligibility rule the auto-match pass itself uses), one page-load query
/// shared by every row so this doesn't turn into N+1 selects.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]
public class UnmatchedPaymentsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    NavBadgeCountService badgeCounts,
    SquarePaymentMatchingService matchingService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>False only when the account belongs to no team at all — a null TeamId now means "all teams merged", not "no context" (2026-07-30, matching the session list).</summary>
    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];

    /// <summary>Label for the team-picker trigger, same shape as the session list's.</summary>
    public string TeamSummaryLabel { get; private set; } = "All teams";

    /// <summary>
    /// Unresolved-payment count per team, for the team picker — same predicate as the table below
    /// (both come from NavBadgeCountService), so a pill's number always equals the row count you get
    /// after clicking it.
    /// </summary>
    public IReadOnlyDictionary<int, int> UnmatchedCountsByTeam { get; private set; } = new Dictionary<int, int>();
    public IReadOnlyList<UnmatchedPaymentRow> UnmatchedPayments { get; private set; } = [];
    public IReadOnlyList<MatchableCandidate> MatchableCandidates { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        // Only worth querying when a picker will actually render (see the Count > 1 guard in the view).
        if (AvailableTeams.Count > 1)
        {
            UnmatchedCountsByTeam = await badgeCounts.GetUnresolvedUnmatchedPaymentsByTeamAsync(
                [.. AvailableTeams.Select(t => t.Id)], HttpContext.RequestAborted);
        }

        // null TeamId == every team this user can see, merged — same convention as the session list.
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);
        HasTeamContext = teamIds is null || teamIds.Count > 0;
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        if (!HasTeamContext)
        {
            return;
        }

        var unmatched = await dbContext.UnmatchedSquarePayments
            .Where(u => (teamIds == null || teamIds.Contains(u.TeamId)) && u.ResolvedUtc == null)
            .OrderBy(u => u.ReceivedUtc)
            .ToListAsync();

        var teamNamesById = AvailableTeams.ToDictionary(t => t.Id, t => t.Name);

        UnmatchedPayments = unmatched.Select(u => new UnmatchedPaymentRow(
            u.Id,
            EasternTimeFormatter.Format(u.ReceivedUtc, "MMM d, yyyy"),
            u.AmountUsd,
            u.BuyerEmailAddress,
            u.SquareOrderId,
            u.TeamId,
            teamNamesById.GetValueOrDefault(u.TeamId, "—"))).ToList();

        if (unmatched.Count == 0)
        {
            return;
        }

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => (teamIds == null || teamIds.Contains(c.Session.TeamId)) && c.Payments.Any(p => p.Status == PaymentStatus.Unpaid))
            .OrderBy(c => c.Name)
            .ToListAsync();

        MatchableCandidates = candidates.Select(c => new MatchableCandidate(
            c.Id,
            c.Name ?? "—",
            EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
            c.Payments.Where(p => p.Status == PaymentStatus.Unpaid).OrderByDescending(p => p.CreatedUtc).First().Amount,
            c.Session.TeamId)).ToList();
    }

    public async Task<IActionResult> OnPostMatchAsync(int unmatchedPaymentId, int candidateId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Defense in depth: re-check the unmatched payment actually belongs to one of this user's
        // teams before acting on a route/form value, same reasoning as every other session-scoped
        // action in this app.
        //
        // This previously read `GetEffectiveTeamIds(user)?.Contains(...) ?? false`, which returns
        // null for a SystemAdmin and so collapsed to `false` — a SystemAdmin got a 403 on every
        // match attempt. It went unnoticed because the page used to force a single selected team;
        // enabling "All teams" (2026-07-30) put SystemAdmins on this path routinely, which is how it
        // surfaced. ResolveViewableTeamIds keeps null meaning "every team", so the check now reads
        // the way it always looked like it read.
        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedPaymentId);
        var viewableTeamIds = accessScope.ResolveViewableTeamIds(user, selectedTeamId: null);
        if (unmatched is null || (viewableTeamIds is not null && !viewableTeamIds.Contains(unmatched.TeamId)))
        {
            return Forbid();
        }

        // A payment and the candidate it's matched to must belong to the same team. Previously
        // implicit — the page could only ever show one team at a time — but "All teams" now renders
        // several teams' payments and candidates on one screen, so without this a tampered (or
        // simply mis-picked) candidateId could attribute Team A's money to Team B's candidate. The
        // view already offers only same-team options; this is the half that can't be bypassed.
        var candidateTeamId = await dbContext.Candidates
            .Where(c => c.Id == candidateId)
            .Select(c => (int?)c.Session.TeamId)
            .FirstOrDefaultAsync();
        if (candidateTeamId != unmatched.TeamId)
        {
            TempData["ErrorMessage"] = "That candidate belongs to a different team than the payment.";
            return RedirectToPage(new { teamId = TeamId });
        }

        var result = await matchingService.ManuallyMatchAsync(unmatchedPaymentId, candidateId, user.Id, CancellationToken.None);
        TempData[result == SquareManualMatchResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            SquareManualMatchResult.Success => "Payment matched.",
            SquareManualMatchResult.AlreadyResolved => "This payment was already matched.",
            SquareManualMatchResult.CandidateNotFound => "Candidate not found.",
            SquareManualMatchResult.NoOutstandingPayment => "That candidate has no outstanding unpaid payment to match against.",
            _ => "Could not match — payment not found."
        };
        // See VecSubmission's OnPostMarkSubmittedAsync — a bare RedirectToPage() drops the teamId
        // query string and strands a multi-team user on the empty "no team context" page.
        return RedirectToPage(new { teamId = unmatched.TeamId });
    }

    public record UnmatchedPaymentRow(int Id, string ReceivedLine, decimal AmountUsd, string? BuyerEmailAddress, string SquareOrderId, int TeamId, string TeamName);
    /// <summary>TeamId is load-bearing, not decorative: with "All teams" the page lists payments and candidates from several teams at once, and the view uses it to offer only same-team candidates for a given payment. OnPostMatchAsync re-checks it server-side regardless.</summary>
    public record MatchableCandidate(int Id, string Name, string SessionDateLine, decimal AmountOwed, int TeamId);
}
