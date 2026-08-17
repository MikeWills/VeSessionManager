using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Phase 6, and <b>local bookkeeping only since #401</b>: expires stale unpaid payments and flags
/// applications stuck Unmatched. Two scan-based passes per run, each with its own tracking field
/// (Payment.ExpiredUnpaid, Candidate.UnmatchedReviewFlaggedUtc) serving as both the "needs action"
/// query filter and the idempotency guard.
///
/// <para><b>Both messages this class used to send are trigger points now.</b> The 5-day FCC fee
/// reminder is <c>MessageTrigger.FccFeeOutstanding</c> and the 10-day expiration notice is
/// <c>MessageTrigger.PaymentUnpaid</c>, each with its threshold expressed in hours on a per-team rule
/// rather than as a constant here — which is what Mike asked for on the issue, since FCC's "date
/// entered" is often not the day they actually received the application. See docs/trigger-points.md.
/// What did <i>not</i> move is the <c>ExpiredUnpaid</c> write below: a team with no rule, or with
/// email switched off, must still not accumulate live payment links that should have gone stale.</para>
///
/// <para>Kept here because it explains a filter the scanners still enforce — the retest gotcha (see
/// docs/payment-reminders.md): a retest Payment's owning Candidate is always ApplicationStatus=Failed
/// (terminal, and permanently so) and has no FCC application of its own to gate on, so
/// ApplicationDateEnteredUtc-based gating can never fire for it. The expiration pass therefore carries
/// a second OR-branch, scoped to Reason=Retest + ApplicationStatus=Failed, anchored on
/// Candidate.ResultMarkedUtc (set by CandidateActionService.MarkFailedAsync) — "the Session Manager
/// marked a result" is the retest's real analogue of "the FCC application was entered."
/// <c>PaymentUnpaidScanner</c> carries the same branch, for the same reason.</para>
///
/// <para>Unmatched candidates never match the expiration pass at all: they have no
/// ApplicationDateEnteredUtc yet (Phase 5 only sets it once Received), which is exactly the "excluded
/// from the triggers, flagged separately instead" behavior the spec calls for, achieved as a side
/// effect of the date-null filter rather than a separate status check.</para>
///
/// Multi-team: one Team per RunAsync call. See docs/multi-team.md.
/// </summary>
public class PaymentReminderService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<PaymentReminderOptions> options,
    ILogger<PaymentReminderService> logger)
{
    // Fixed by the spec's own feature names ("5-day reminder", "10-day expiration").
    //
    // ExpirationThresholdDays is still what this class expires on. ReminderThresholdDays no longer
    // drives anything here — the FCC fee reminder reads its own rule's ParameterHours — and survives
    // only because the Applicant Status page colours its "days pending" column on both boundaries
    // (2026-07-30): amber once the reminder is due, red once the payment is due to expire. Those
    // colours are meant to *explain* what the app does, so a page reading a constant while a team
    // sets its own hours would show a red row on a day nothing happens. PR2 of #401 makes both
    // per-team and resolves it there; this is the one place PR1 knowingly leaves half-migrated.
    public const int ReminderThresholdDays = 5;
    public const int ExpirationThresholdDays = 10;

    public async Task<PaymentReminderResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new PaymentReminderResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // No EmailSettings check any more, and no mute check: neither pass below sends anything.
        // Both are local bookkeeping, which is precisely why they stayed here when the two messages
        // moved onto rules — a team with no rules, or with email switched off, must still not
        // accumulate live payment links that should have gone stale.
        await ProcessExpirationsAsync(team, now, result, cancellationToken);

        await FlagStaleUnmatchedCandidatesAsync(team, now, result, cancellationToken);

        logger.LogInformation("Payment reminder run finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Marks a stale unpaid payment expired. <b>Local bookkeeping only since #401</b> — the notice
    /// that used to go out beside this write is <c>MessageTrigger.PaymentUnpaid</c> now, so a team can
    /// decide who hears about it, when, and whether at all.
    ///
    /// <para><b>The write stayed here rather than moving with the message</b>, and that split is the
    /// point: the flag is what stops a dead payment link being treated as live, and it has to keep
    /// happening for a team that has no rule for it and for one that has email switched off. Note the
    /// consequence for the scanner — by the time <c>PaymentUnpaidScanner</c> looks, this flag is
    /// usually already set, so that query must not filter on it.</para>
    /// </summary>
    private async Task ProcessExpirationsAsync(Team team, DateTime now, PaymentReminderResult result, CancellationToken cancellationToken)
    {
        var threshold = now.AddDays(-ExpirationThresholdDays);
        var paymentCutoff = PaymentEligibilityWindow.CutoffUtc(now);

        var payments = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session)
            .Where(p => p.Status == PaymentStatus.Unpaid
                        && !p.ExpiredUnpaid
                        && p.Candidate.Session.TeamId == team.Id
                        && p.Candidate.Session.Status == SessionStatus.Active
                        // Status == Active means "not cancelled", not "not finished" — without an
                        // age bound this reaches the historical import's backfilled candidates and
                        // would email them about payments for sessions they sat months ago.
                        // See PaymentEligibilityWindow.
                        && p.Candidate.Session.ScheduledStartUtc >= paymentCutoff
                        && ((!CandidateApplicationStatusExtensions.TerminalStatuses.Contains(p.Candidate.ApplicationStatus)
                                && p.Candidate.ApplicationDateEnteredUtc != null
                                && p.Candidate.ApplicationDateEnteredUtc <= threshold)
                            || (p.Reason == PaymentReason.Retest
                                && p.Candidate.ApplicationStatus == CandidateApplicationStatus.Failed
                                && p.Candidate.ResultMarkedUtc != null
                                && p.Candidate.ResultMarkedUtc <= threshold)))
            .ToListAsync(cancellationToken);

        foreach (var payment in payments)
        {
            // "Stop further reminders for that payment" (spec) — ExpiredUnpaid = true removes it
            // from this same query on every future run. Saved per item, like every scan-based pass
            // here, so a crash mid-run keeps the progress already made.
            payment.ExpiredUnpaid = true;
            result.ExpirationsProcessed++;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task FlagStaleUnmatchedCandidatesAsync(Team team, DateTime now, PaymentReminderResult result, CancellationToken cancellationToken)
    {
        var threshold = now.AddDays(-options.Value.UnmatchedReviewWindowDays);

        var candidates = await dbContext.Candidates
            .Where(c => c.PiiPurgedUtc == null
                        && c.ApplicationStatus == CandidateApplicationStatus.Unmatched
                        && c.UnmatchedReviewFlaggedUtc == null
                        && c.DateRegisteredUtc <= threshold
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.UnmatchedReviewFlaggedUtc = now;
            result.CandidatesFlaggedForReview++;
            logger.LogWarning("Candidate {CandidateId} still Unmatched {WindowDays}+ days after registration — flagged for manual FCC/FRN review", candidate.Id, options.Value.UnmatchedReviewWindowDays);
        }

        if (candidates.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
