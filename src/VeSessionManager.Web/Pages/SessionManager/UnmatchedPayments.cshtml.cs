using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
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
    SquarePaymentMatchingService matchingService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<UnmatchedPaymentRow> UnmatchedPayments { get; private set; } = [];
    public IReadOnlyList<MatchableCandidate> MatchableCandidates { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        var teamId = accessScope.TryResolveViewableTeamId(user, TeamId, AvailableTeams);
        TeamId = teamId;
        HasTeamContext = teamId is not null;
        if (teamId is not int id)
        {
            return;
        }

        var unmatched = await dbContext.UnmatchedSquarePayments
            .Where(u => u.TeamId == id && u.ResolvedUtc == null)
            .OrderBy(u => u.ReceivedUtc)
            .ToListAsync();

        UnmatchedPayments = unmatched.Select(u => new UnmatchedPaymentRow(
            u.Id,
            u.ReceivedUtc.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) + " UTC",
            u.AmountUsd,
            u.BuyerEmailAddress,
            u.SquareOrderId)).ToList();

        if (unmatched.Count == 0)
        {
            return;
        }

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => c.Session.TeamId == id && c.Payments.Any(p => p.Status == PaymentStatus.Unpaid))
            .OrderBy(c => c.Name)
            .ToListAsync();

        MatchableCandidates = candidates.Select(c => new MatchableCandidate(
            c.Id,
            c.Name ?? "—",
            c.Session.ScheduledStartUtc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            c.Payments.Where(p => p.Status == PaymentStatus.Unpaid).OrderByDescending(p => p.CreatedUtc).First().Amount)).ToList();
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
        // action in this app. Preserves this page's pre-existing behavior exactly (including for
        // SystemAdmin, whose null effective-team-set never matches here) — not the place to revisit
        // that, out of scope for this change.
        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedPaymentId);
        if (unmatched is null || !(accessScope.GetEffectiveTeamIds(user)?.Contains(unmatched.TeamId) ?? false))
        {
            return Forbid();
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
        return RedirectToPage();
    }

    public record UnmatchedPaymentRow(int Id, string ReceivedLine, decimal AmountUsd, string? BuyerEmailAddress, string SquareOrderId);
    public record MatchableCandidate(int Id, string Name, string SessionDateLine, decimal AmountOwed);
}
