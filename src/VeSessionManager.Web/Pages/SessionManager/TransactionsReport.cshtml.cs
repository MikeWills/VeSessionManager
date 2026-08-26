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

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Every real money movement — a payment actually taken, and every refund attempted against it —
/// tied back to the candidate it was for (Mike, 2026-08-26, after live-verifying payments and
/// refunds work end to end: "we might want a report that shows all transactions refunds and
/// payments and that will need to tie to the candidate").
///
/// <para><b>Why this can exist at all.</b> <c>Candidate.Name</c> does not survive PII purge —
/// scheduled retention, or immediately on a no-show/withdrawal — so a report reading it directly
/// would go blank for exactly the candidates most likely to have a refund (a withdrawal is often
/// the reason for one). <see cref="Payment.CandidateNameSnapshot"/> exists so this page has
/// something to read regardless of what has since happened to the Candidate row.</para>
///
/// <para><b>Only Paid payments are rows here.</b> Unpaid and NotApplicable payments never moved
/// money, so they are not transactions — this report answers "what actually happened to money",
/// not "what payments exist". A refund is included whatever its outcome (Pending/Completed/
/// Rejected/Failed): a rejected attempt is still something that happened and is worth being able
/// to see, even though no money moved for it.</para>
///
/// <para>Admin-only, same gate as the other Reports-menu pages (Stats, VE Session Counts,
/// Auditioning Report) — this is more sensitive than any of those.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
[RemembersFilters]
public class TransactionsReportModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateTo { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public string TeamSummaryLabel { get; private set; } = "All teams";

    public IReadOnlyList<TransactionRow> Rows { get; private set; } = [];

    /// <summary>The two amount columns' totals, for the line at the bottom of the table — what a report like this exists to answer without reaching for a calculator.</summary>
    public string TotalPaidLine { get; private set; } = Usd.Format(0m);
    public string TotalRefundedLine { get; private set; } = Usd.Format(0m);
    public string NetLine { get; private set; } = Usd.Format(0m);

    public async Task OnGetAsync()
    {
        var user = await userManager.GetRequiredCachedUserAsync(dbContext, HttpContext, User);

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);
        if (teamIds is not null && teamIds.Count == 0)
        {
            return;
        }

        // Inclusive on both ends — a Session Manager picking "Aug 1 to Aug 31" means the whole of
        // the 31st, not midnight at its start. Dates are compared against the moment the money
        // actually moved (PaidDateUtc / Refund.RequestedUtc), not CreatedUtc.
        var fromUtc = DateFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = DateTo?.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var payments = await dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.Team)
            .Include(p => p.Refunds)
            .Where(p => (teamIds == null || teamIds.Contains(p.Candidate.Session.TeamId))
                        && p.Status == PaymentStatus.Paid)
            .ToListAsync(HttpContext.RequestAborted);

        Rows = BuildRows(payments, fromUtc, toUtc);

        var totalPaid = Rows.Where(r => r.TypeLabel == "Payment").Sum(r => r.SignedAmountUsd);
        var totalRefunded = -Rows.Where(r => r.TypeLabel == "Refund").Sum(r => r.SignedAmountUsd);
        TotalPaidLine = Usd.Format(totalPaid);
        TotalRefundedLine = Usd.Format(totalRefunded);
        NetLine = Usd.Format(totalPaid - totalRefunded);
    }

    /// <summary>
    /// The actual report logic, pulled out of <see cref="OnGetAsync"/> so it is testable without a
    /// database, an <see cref="HttpContext"/>, or a signed-in user — everything else on this page is
    /// just wiring that decides which <see cref="Payment"/> rows this method sees.
    /// </summary>
    /// <param name="payments">Already scoped to Paid, team, and whatever else the caller wants —
    /// this method does not re-filter by team or status, only by date.</param>
    internal static IReadOnlyList<TransactionRow> BuildRows(IEnumerable<Payment> payments, DateTime? fromUtc, DateTime? toUtc)
    {
        var rows = new List<TransactionRow>();
        foreach (var payment in payments)
        {
            var candidate = payment.Candidate;
            var session = candidate.Session;
            // The snapshot is the point of this whole page — it is what still has a name once
            // Candidate.Name is gone. Falling back to the live Candidate.Name covers a payment row
            // that predates this column (nothing backfills a value nobody captured); falling back
            // again to "—" covers the two-purges-deep case where neither survived.
            var name = payment.CandidateNameSnapshot ?? candidate.Name ?? "—";
            var paidUtc = payment.PaidDateUtc ?? payment.CreatedUtc;

            if ((fromUtc is null || paidUtc >= fromUtc) && (toUtc is null || paidUtc <= toUtc))
            {
                rows.Add(new TransactionRow(
                    candidate.Id,
                    name,
                    session.Id,
                    session.Title,
                    EasternTimeFormatter.Format(session.ScheduledStartUtc, "MMM d, yyyy"),
                    session.Team.Name,
                    "Payment",
                    EasternTimeFormatter.Format(paidUtc, "MMM d, yyyy h:mm tt"),
                    paidUtc.ToString("o", CultureInfo.InvariantCulture),
                    Usd.Format(payment.SquareAmountPaidUsd ?? payment.Amount),
                    // Positive for a payment, negative for a refund — what the totals line sums.
                    payment.SquareAmountPaidUsd ?? payment.Amount,
                    "chip-green",
                    "Paid"));
            }

            foreach (var refund in payment.Refunds)
            {
                if ((fromUtc is not null && refund.RequestedUtc < fromUtc) || (toUtc is not null && refund.RequestedUtc > toUtc))
                {
                    continue;
                }

                var (chipClass, chipLabel) = SessionChips.Refund(refund.Status)!.Value;
                // Only a Completed refund actually moved money back out; Pending/Rejected/Failed
                // are shown (this is a record of what was attempted, not just what settled) but
                // contribute nothing to the totals line until Square says otherwise.
                var signedAmount = refund.Status == RefundStatus.Completed ? -refund.AmountUsd : 0m;

                rows.Add(new TransactionRow(
                    candidate.Id,
                    name,
                    session.Id,
                    session.Title,
                    EasternTimeFormatter.Format(session.ScheduledStartUtc, "MMM d, yyyy"),
                    session.Team.Name,
                    "Refund",
                    EasternTimeFormatter.Format(refund.RequestedUtc, "MMM d, yyyy h:mm tt"),
                    refund.RequestedUtc.ToString("o", CultureInfo.InvariantCulture),
                    $"-{Usd.Format(refund.AmountUsd)}",
                    signedAmount,
                    chipClass,
                    chipLabel));
            }
        }

        return [.. rows.OrderByDescending(r => r.DateSortValue)];
    }

    /// <summary>The report as a file — same shape as Auditioning Report's export, for the same reason: this is exactly the kind of thing reviewed away from the screen, or handed to someone who isn't a Session Manager at all (a treasurer, an auditor).</summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        await OnGetAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Date,Type,Candidate,Session,Team,Amount,Status");

        foreach (var row in Rows)
        {
            csv.AppendLine(CsvExport.Row(
                row.DateSortValue,
                row.TypeLabel,
                row.CandidateName,
                row.SessionTitle,
                row.TeamName,
                row.SignedAmountUsd.ToString("F2", CultureInfo.InvariantCulture),
                row.StatusLabel));
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(CsvExport.ToBytes(csv), "text/csv", $"transactions-report-{stamp}.csv");
    }

    /// <param name="SignedAmountUsd">Positive for a payment, negative for a completed refund, zero for a refund that hasn't (or won't) move money — what the totals line and the CSV export both sum, so they can never disagree with what the formatted AmountLine shows.</param>
    public record TransactionRow(
        int CandidateId,
        string CandidateName,
        int SessionId,
        string SessionTitle,
        string SessionDateLine,
        string TeamName,
        string TypeLabel,
        string DateLine,
        string DateSortValue,
        string AmountLine,
        decimal SignedAmountUsd,
        string StatusChipClass,
        string StatusLabel);
}
