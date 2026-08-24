using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core;
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
[Authorize(Roles = RoleGroups.SessionStaff)]
[RemembersFilters]
public class UnmatchedPaymentsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    NavBadgeCountService badgeCounts,
    SquarePaymentMatchingService matchingService,
    RefundService refundService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>
    /// Show rows already dismissed (#99) instead of the ones still waiting. Off by default: the page
    /// exists to be emptied, and a dismissed row that stayed in the main list would defeat the point
    /// of dismissing it. Matched rows are not shown either way — those became real Payments and are
    /// visible on the candidate.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public bool ShowDismissed { get; set; }

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
        var user = await userManager.GetRequiredUserAsync(dbContext, User);

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

        // A dismissed row is ResolvedUtc set with no MatchedPaymentId — see UnmatchedSquarePayment's
        // remarks. Filtering on MatchedPaymentId alone would also sweep in matched rows, which belong
        // on the candidate, not here.
        var query = dbContext.UnmatchedSquarePayments
            .Where(u => teamIds == null || teamIds.Contains(u.TeamId));
        query = ShowDismissed
            ? query.Where(u => u.ResolvedUtc != null && u.MatchedPaymentId == null)
            : query.Where(u => u.ResolvedUtc == null);

        var unmatched = await query
            .Include(u => u.ResolvedByUser)
            .Include(u => u.Refunds)
            .OrderBy(u => u.ReceivedUtc)
            .ToListAsync();

        var teamNamesById = AvailableTeams.ToDictionary(t => t.Id, t => t.Name);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        UnmatchedPayments = unmatched.Select(u => new UnmatchedPaymentRow(
            u.Id,
            EasternTimeFormatter.Format(u.ReceivedUtc, "MMM d, yyyy"),
            u.ReceivedUtc.ToString("o", CultureInfo.InvariantCulture),
            u.AmountUsd,
            u.BuyerEmailAddress,
            u.SquareOrderId,
            u.TeamId,
            teamNamesById.GetValueOrDefault(u.TeamId, "—"),
            u.ResolvedUtc is { } resolved ? EasternTimeFormatter.Format(resolved, "MMM d, yyyy") : null,
            u.ResolvedByUser?.Name,
            u.ResolutionNote,
            // An unmatched payment is money that arrived, so it is "paid" by definition and always
            // carries the Square payment id — which is exactly why this half of #375 needed no
            // schema change. What the eligibility check still earns here is the one-year window and
            // the remainder after a previous attempt, since a refund Square refused leaves a row
            // behind and the amount must not be double-counted against it.
            RefundEligibility.For(isPaid: true, u.SquarePaymentId, u.AmountUsd, u.ReceivedUtc, u.Refunds, nowUtc)
        )).ToList();

        // Nothing below is for the dismissed view — those rows are resolved and offer no action.
        if (unmatched.Count == 0 || ShowDismissed)
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

    /// <summary>
    /// Dismiss an unmatched payment without matching it (#99).
    ///
    /// <para>Same defense-in-depth team re-check as OnPostMatchAsync — see the reasoning there,
    /// including why this must be ResolveViewableTeamIds and not GetEffectiveTeamIds.</para>
    /// </summary>
    public async Task<IActionResult> OnPostDismissAsync(int unmatchedPaymentId, string? reason)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedPaymentId);
        var viewableTeamIds = accessScope.ResolveViewableTeamIds(user, selectedTeamId: null);
        if (unmatched is null || (viewableTeamIds is not null && !viewableTeamIds.Contains(unmatched.TeamId)))
        {
            return Forbid();
        }

        var result = await matchingService.DismissAsync(unmatchedPaymentId, reason, user.Id, CancellationToken.None);
        TempData[result == SquareManualMatchResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            SquareManualMatchResult.Success => "Payment dismissed. Nothing was refunded in Square.",
            SquareManualMatchResult.AlreadyResolved => "This payment was already resolved.",
            // Neither arm below is reachable from a dismissal — DismissAsync never looks at a
            // candidate — but the switch stays exhaustive so a future member can't fall silently
            // into the default and render as "not found".
            SquareManualMatchResult.CandidateNotFound => "Candidate not found.",
            SquareManualMatchResult.NoOutstandingPayment => "That candidate has no outstanding unpaid payment.",
            _ => "Could not dismiss — payment not found."
        };
        return RedirectToPage(new { teamId = unmatched.TeamId });
    }

    /// <summary>
    /// Refund the payment through Square and then dismiss the row (#375) — the action this screen has
    /// wanted since #99, when the dismiss modal had to say <b>in bold</b> that dismissing refunds
    /// nothing, because there was no way to.
    ///
    /// <para><b>Refund first, dismiss only if it worked.</b> The order is the whole design: dismissing
    /// clears the row from the one screen that lists this money, so dismissing after a failed refund
    /// would hide a payment that is still sitting in the merchant account with nobody looking for it.
    /// A failed refund therefore leaves the row exactly where it was, which is also where the user
    /// will look for it.</para>
    ///
    /// <para>The dismissal reason records the refund rather than asking for one — "refunded
    /// {amount}" is more use to whoever reads this back than whatever free text would have been
    /// typed, and it is the same sentence in the audit log.</para>
    /// </summary>
    public async Task<IActionResult> OnPostRefundAndDismissAsync(int unmatchedPaymentId, string? amount, string? reason)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Same defense-in-depth team re-check as the two handlers above, and for the strongest
        // version of the reason they give: this one moves real money out of a merchant account, so a
        // posted id from outside the user's teams must not merely fail to render — it must Forbid.
        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedPaymentId);
        var viewableTeamIds = accessScope.ResolveViewableTeamIds(user, selectedTeamId: null);
        if (unmatched is null || (viewableTeamIds is not null && !viewableTeamIds.Contains(unmatched.TeamId)))
        {
            return Forbid();
        }

        // Usd.TryParse, not decimal.TryParse — the latter reads "12.50" as 1250 under a
        // comma-decimal culture (CLAUDE.md's Usd entry).
        if (!Usd.TryParse(amount, out var amountUsd))
        {
            TempData["ErrorMessage"] = "Enter a refund amount, in dollars.";
            return RedirectToPage(new { teamId = unmatched.TeamId });
        }

        var refund = await refundService.RefundUnmatchedPaymentAsync(
            unmatchedPaymentId, amountUsd, reason, user.Id, CancellationToken.None);
        var refundOutcome = ActionOutcomes.IssueRefund(refund, amountUsd);

        if (!refundOutcome.Success)
        {
            TempData["ErrorMessage"] = refundOutcome.Message;
            return RedirectToPage(new { teamId = unmatched.TeamId });
        }

        var dismissal = await matchingService.DismissAsync(
            unmatchedPaymentId, $"Refunded {Usd.Format(amountUsd)} through Square.", user.Id, CancellationToken.None);

        TempData[dismissal == SquareManualMatchResult.Success ? "StatusMessage" : "ErrorMessage"] =
            dismissal == SquareManualMatchResult.Success
                ? $"{refundOutcome.Message} Payment dismissed."
                // The refund went through and the dismissal did not — an odd pair, and worth saying
                // plainly rather than reporting the whole thing as a failure. Re-dismissing is safe;
                // re-refunding would not be, which is why the sentence says which half to redo.
                : $"{refundOutcome.Message} The payment could not be dismissed from this list — dismiss it separately. Do not refund it again.";

        return RedirectToPage(new { teamId = unmatched.TeamId });
    }

    /// <summary>ReceivedSortValue carries the raw timestamp behind ReceivedLine for the table's click-to-sort header (see app.js) — "MMM d, yyyy" sorts alphabetically as text, putting Apr before Mar.</summary>
    /// <param name="DismissedLine">Non-null only in the dismissed view.</param>
    /// <param name="Refundability">Whether "Refund and dismiss" is offered, and for how much (#375).</param>
    public record UnmatchedPaymentRow(
        int Id, string ReceivedLine, string ReceivedSortValue, decimal AmountUsd, string? BuyerEmailAddress,
        string SquareOrderId, int TeamId, string TeamName,
        string? DismissedLine = null, string? DismissedByName = null, string? ResolutionNote = null,
        RefundEligibility Refundability = default)
    {
        /// <summary>The refundable amount as a plain "12.50" for the amount input — Usd.Raw, never a bare :F2 under an ambient culture.</summary>
        public string RefundableRaw => Usd.Raw(Refundability.RemainingUsd);
    }
    /// <summary>TeamId is load-bearing, not decorative: with "All teams" the page lists payments and candidates from several teams at once, and the view uses it to offer only same-team candidates for a given payment. OnPostMatchAsync re-checks it server-side regardless.</summary>
    public record MatchableCandidate(int Id, string Name, string SessionDateLine, decimal AmountOwed, int TeamId);
}
