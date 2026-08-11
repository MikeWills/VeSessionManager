using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Phase 6: nudges candidates with an outstanding fee, flags stale unpaid applications, and
/// notifies the Session Manager at expiration. Three independent scan-based passes per run, same
/// shape as every earlier phase — each pass' own tracking field (Payment.PaymentReminderSentUtc,
/// Payment.ExpiredUnpaid, Candidate.UnmatchedReviewFlaggedUtc) is both the "needs action" query
/// filter and the idempotency guard, so a crash mid-run or a re-run never double-sends/double-flags.
///
///   - 5-day FCC fee reminder: Candidate whose FccPaymentStatus is PendingVerification and whose
///     ApplicationDateEnteredUtc is at least 5 days old -> FccFeeReminder5Day to the candidate.
///     This is FCC's own application fee, paid at CORES; see the note below.
///   - 10-day expiration: Unpaid Payment whose Candidate has an ApplicationDateEnteredUtc at least
///     10 days old -> ExpiredUnpaid = true, PaymentExpirationNotice to EmailSettings.AdminNotificationEmail
///     (the Session Manager, not the candidate — per the spec).
///   - Unmatched review flag: Candidate still Unmatched more than PaymentReminderOptions.
///     UnmatchedReviewWindowDays past DateRegisteredUtc -> UnmatchedReviewFlaggedUtc set, logged as
///     a WARNING (no admin UI to surface this list yet, so the log is the only visibility).
///
/// <para><b>The 5-day reminder is about FCC's fee, not the team's (#219, corrected 2026-08-11).</b>
/// It used to fire on an unpaid Square <see cref="Payment"/> — the VEC exam session fee. But that fee
/// is collected before or at the session, and this trigger cannot fire until FCC has received the
/// application plus five days, by which point the money has been in hand for over a week. It could
/// only ever fire for a payment that slipped through, and it fired carrying a Square link for a bill
/// the candidate had usually already settled. What is actually outstanding at that moment is <b>FCC's
/// application fee</b>, which the applicant pays directly to the FCC through CORES and which this app
/// never touches.</para>
///
/// <para>The signal was already being collected and read by nothing but a display column:
/// <c>UlsWatcherService.ResolvePaymentStatus</c> maps ULS's <c>FVPOFF</c> (fee validation open) to
/// <see cref="FccApplicationPaymentStatus.PendingVerification"/> twice daily, per candidate. That is
/// exactly "the FCC fee is due", from FCC itself, so the reminder now reads it rather than inferring
/// a different fee's state.</para>
///
/// <para><b>The template carries no payment link at all</b>, deliberately. There is nothing to link
/// to: FCC bills the applicant. Offering the team's Square link here would be pointing them at the
/// wrong bill, which is the original defect rather than a fix for it. It also disposes of #218 — an
/// empty <c>href</c> cannot ship from a template with no link in it.</para>
///
/// <para>The retest branch disappears from this pass with the payment it hung off. A retest has no
/// FCC application of its own, so it never has an FCC fee outstanding; the ApplicationDateEnteredUtc
/// gymnastics that existed to make retests work here have nothing left to do. The expiration pass
/// below still carries them, because it is still about the Square payment.</para>
///
/// Both money-passes share the same base exclusions per the spec: NotApplicable payments and a
/// Cancelled session. Terminal Candidate.ApplicationStatus (Granted/Failed/NotTested) is excluded
/// too, *except* for a Reason=Retest payment — see the retest gotcha below. Unmatched candidates
/// naturally never match either pass — they have no ApplicationDateEnteredUtc yet (Phase 5 only
/// sets it once Received) — which is exactly the "excluded from both triggers, flagged separately
/// instead" behavior the spec calls for, achieved here as a side effect of the date-null filter
/// rather than a separate status check.
///
/// Retest gotcha (see docs/payment-reminders.md): a retest Payment's owning
/// Candidate is always ApplicationStatus=Failed (terminal, and permanently so — nothing in this app
/// ever moves a Candidate off Failed once set) and has no FCC application of its own to gate on, so
/// ApplicationDateEnteredUtc-based gating can never fire for it. Both passes therefore carry a
/// second OR-branch, scoped to Reason=Retest + ApplicationStatus=Failed, anchored on
/// Candidate.ResultMarkedUtc (set by CandidateActionService.MarkFailedAsync) instead of
/// ApplicationDateEnteredUtc — "the Session Manager marked a result" is the retest's real analogue
/// of "the FCC application was entered." The InitialExam branch is untouched by this.
///
/// Multi-team: this service now operates on one Team's candidates/payments per RunAsync call —
/// each team has its own separate SMTP account and EmailSettings/EmailTemplate rows. See
/// docs/multi-team.md.
/// </summary>
public class PaymentReminderService(
    AppDbContext dbContext,
    EmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    IOptions<PaymentReminderOptions> options,
    ILogger<PaymentReminderService> logger)
{
    /// <summary>New key rather than reused copy: the old PaymentReminder5Day body is about a different fee, and a deployment that customised it must not have that text silently repurposed. See EmailDefaultsSeeder.</summary>
    public const string FccFeeReminder5DayKey = "FccFeeReminder5Day";
    private const string PaymentExpirationNoticeKey = "PaymentExpirationNotice";

    // Fixed by the spec's own feature names ("5-day reminder", "10-day expiration") — unlike
    // UnmatchedReviewWindowDays, these are not meant to be admin-configurable.
    //
    // Public because the Applicant Status page colours its "days pending" column on exactly these
    // boundaries (2026-07-30): amber once the 5-day reminder is due, red once the payment is due to
    // expire. Those colours are meant to *explain* what this service already does, so they have to
    // read the same numbers rather than restate them — a drift would show a Session Manager a red
    // row on a day nothing actually happens.
    public const int ReminderThresholdDays = 5;
    public const int ExpirationThresholdDays = 10;

    public async Task<PaymentReminderResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new PaymentReminderResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            logger.LogWarning("No EmailSettings row exists yet for team {TeamId} — skipping payment reminders/expirations until seeded", team.Id);
        }
        else
        {
            await SendFccFeeRemindersAsync(team, now, emailSettings, result, cancellationToken);
            await ProcessExpirationsAsync(team, now, emailSettings, result, cancellationToken);
        }

        await FlagStaleUnmatchedCandidatesAsync(team, now, result, cancellationToken);

        logger.LogInformation("Payment reminder run finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Reminds a candidate that FCC is still waiting on its own application fee — see the note on the
    /// class. Scans Candidates, not Payments: this fee never passes through the app.
    /// </summary>
    private async Task SendFccFeeRemindersAsync(Team team, DateTime now, EmailSettings emailSettings, PaymentReminderResult result, CancellationToken cancellationToken)
    {
        var threshold = now.AddDays(-ReminderThresholdDays);
        var paymentCutoff = PaymentEligibilityWindow.CutoffUtc(now);

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.FccPaymentStatus == FccApplicationPaymentStatus.PendingVerification
                        && c.FccFeeReminderSentUtc == null
                        && c.PiiPurgedUtc == null
                        && c.Email != null
                        // FCC's own clock, and the only date that means anything here: the fee falls
                        // due when the application is received, not when the exam was sat.
                        && c.ApplicationDateEnteredUtc != null
                        && c.ApplicationDateEnteredUtc <= threshold
                        // A terminal candidate has no live application for a fee to be outstanding
                        // on. PendingVerification should already have cleared, but ULS is a mirror
                        // polled twice a day and this costs nothing.
                        && !CandidateApplicationStatusExtensions.TerminalStatuses.Contains(c.ApplicationStatus)
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        // Status == Active means "not cancelled", not "not finished" — without an
                        // age bound this reaches the historical import's backfilled candidates and
                        // would email them about sessions they sat months ago.
                        // See PaymentEligibilityWindow.
                        && c.Session.ScheduledStartUtc >= paymentCutoff)
            .ToListAsync(cancellationToken);

        if (candidates.Count > 0 && !team.IsEmailConfigured)
        {
            // SMTP optional, same reasoning as CandidateNotificationService — skip quietly rather
            // than fail-log every poll; FccFeeReminderSentUtc stays null so the next poll sends
            // everything backlogged once SMTP is configured.
            logger.LogInformation("SMTP is not fully configured for team {TeamId} — {PendingCount} FCC fee reminder(s) waiting; will send automatically once configured", team.Id, candidates.Count);
            return;
        }

        var credentials = team.ToEmailCredentials();

        foreach (var candidate in candidates)
        {
            try
            {
                var placeholders = new Dictionary<string, string>
                {
                    ["CandidateName"] = candidate.Name ?? "",
                    ["SessionDate"] = FormatSessionDate(candidate.Session.ScheduledStartUtc),
                    // The FRN is what CORES asks for, so a reminder that omits it sends the reader
                    // hunting for it. Public FCC data, not PII — see the FRN note in CLAUDE.md.
                    ["Frn"] = candidate.Frn ?? "",
                    // No payment link, and no placeholder that could become one. FCC bills the
                    // applicant directly; the team's Square link pays a different bill.
                    ["FccApplicationFileNumber"] = candidate.UlsApplicationFileNumber ?? ""
                };

                var rendered = await templateRenderer.RenderAsync(team.Id, FccFeeReminder5DayKey, placeholders, cancellationToken);
                if (rendered is null)
                {
                    result.Failed++;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                await emailSender.SendAsync(
                    credentials,
                    // Candidate-facing, so it carries the team's monitoring copy (issue #207).
                    new EmailMessage(candidate.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName,
                        emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo,
                        BccAddress: emailSettings.BccAddress),
                    cancellationToken);

                candidate.FccFeeReminderSentUtc = now;
                result.RemindersSent++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                logger.LogError(ex, "Failed to send FccFeeReminder5Day for Candidate {CandidateId}", candidate.Id);
            }

            // Save after every item so a crash mid-run, or one failure, never loses progress
            // already made on others or resends to someone already reminded.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ProcessExpirationsAsync(Team team, DateTime now, EmailSettings emailSettings, PaymentReminderResult result, CancellationToken cancellationToken)
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

        if (payments.Count > 0 && !team.IsEmailConfigured)
        {
            logger.LogInformation("SMTP is not fully configured for team {TeamId} — {PendingCount} payment expiration notice(s) waiting; will send automatically once configured", team.Id, payments.Count);
            return;
        }

        var credentials = team.ToEmailCredentials();

        foreach (var payment in payments)
        {
            try
            {
                var placeholders = new Dictionary<string, string>
                {
                    ["CandidateName"] = payment.Candidate.Name ?? "",
                    ["SessionDate"] = FormatSessionDate(payment.Candidate.Session.ScheduledStartUtc),
                    // Not "C"/CultureInfo.InvariantCulture — the invariant culture's currency
                    // symbol is the generic "¤", not "$". This app is US-only (FCC/ARRL), so a
                    // literal "$" prefix is simpler and more correct than culture-driven formatting.
                    ["PaymentAmount"] = $"${payment.Amount.ToString("F2", CultureInfo.InvariantCulture)}"
                };

                var rendered = await templateRenderer.RenderAsync(team.Id, PaymentExpirationNoticeKey, placeholders, cancellationToken);
                if (rendered is null)
                {
                    result.Failed++;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                await emailSender.SendAsync(
                    credentials,
                    // Deliberately no BccAddress: this notice already goes to the team's own
                    // AdminNotificationEmail. Copying a team's internal mail to the same team's
                    // monitoring inbox is noise, and the BCC exists to watch what *candidates*
                    // receive.
                    new EmailMessage(emailSettings.AdminNotificationEmail, emailSettings.FromAddress, emailSettings.FromDisplayName, emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo),
                    cancellationToken);

                // "Stop further reminders for that payment" (spec) — ExpiredUnpaid = true removes
                // it from this same query on every future run, and it was never eligible for the
                // 5-day reminder query again either (Unpaid stays true, but a real deployment's
                // 5-day pass will have already fired days earlier in the normal case).
                payment.ExpiredUnpaid = true;
                result.ExpirationsProcessed++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                logger.LogError(ex, "Failed to send PaymentExpirationNotice for Payment {PaymentId}", payment.Id);
            }

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

    /// <summary>
    /// Delegates to SessionTimeFormatter so both services — and both halves of what a candidate
    /// receives — cannot disagree about the wording. This used to be a byte-identical copy in each
    /// file, rendering UTC.
    /// </summary>
    private static string FormatSessionDate(DateTime scheduledStartUtc) =>
        SessionTimeFormatter.ForCandidate(scheduledStartUtc);
}
