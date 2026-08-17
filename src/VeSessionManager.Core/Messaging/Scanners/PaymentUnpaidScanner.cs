using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "This exam fee has been unpaid for N hours." Replaces the notice half of
/// <c>PaymentReminderService.ProcessExpirationsAsync</c>, whose 10 days is now a rule's
/// <c>ParameterHours</c> and whose recipient — the team's own admin inbox — is now a rule's
/// <c>Recipient</c> rather than a special case in the send path.
///
/// <para><b>The expiry write did not move, and must not.</b>
/// <c>ProcessExpirationsAsync</c> still sets <c>Payment.ExpiredUnpaid = true</c> on its own schedule,
/// because that is local bookkeeping that has to keep happening whether or not a team has a rule for
/// it: a team with no rule, or with email switched off, must not accumulate live payment links that
/// should have gone stale.</para>
///
/// <para><b>Which is exactly why this query does not filter on <c>!ExpiredUnpaid</c>.</b> It is the
/// single easiest line in this PR to add by reflex — the old query had it, and the two passes used to
/// be one. By the time a rule fires, that flag is usually already set by the pass above, so filtering
/// on it would mean the notice silently never went out. Idempotency here comes from the
/// <see cref="MessageRuleRun"/> marker, which is the whole point of the marker.</para>
/// </summary>
public class PaymentUnpaidScanner(AppDbContext dbContext) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.PaymentUnpaid;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var parameterHours = MessageTriggerDefinitions.ParameterHoursOrDefault(rule);
        var dueThresholdUtc = nowUtc.AddHours(-parameterHours);
        var earliestAnchorUtc = rule.CreatedUtc.AddHours(-parameterHours);
        var paymentCutoff = PaymentEligibilityWindow.CutoffUtc(nowUtc);

        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var payments = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session)
            .Where(p => p.Status == PaymentStatus.Unpaid
                        && !settled.Contains(p.Id)
                        && p.Candidate.PiiPurgedUtc == null
                        && p.Candidate.Session.TeamId == team.Id
                        && p.Candidate.Session.Status == SessionStatus.Active
                        // Status == Active means "not cancelled", not "not finished" — without an
                        // age bound this reaches the historical import's backfilled candidates and
                        // would email about payments for sessions they sat months ago.
                        // See PaymentEligibilityWindow.
                        && p.Candidate.Session.ScheduledStartUtc >= paymentCutoff
                        && (onlySessionId == null || p.Candidate.SessionId == onlySessionId)
                        // Both branches, and the second one is not optional. A retest Payment's
                        // owning Candidate is always ApplicationStatus=Failed (terminal, and
                        // permanently so) and has no FCC application of its own, so
                        // ApplicationDateEnteredUtc gating can never fire for it — "the Session
                        // Manager marked a result" is the retest's real analogue. See
                        // docs/payment-reminders.md.
                        && ((!CandidateApplicationStatusExtensions.TerminalStatuses.Contains(p.Candidate.ApplicationStatus)
                                && p.Candidate.ApplicationDateEnteredUtc != null
                                && p.Candidate.ApplicationDateEnteredUtc <= dueThresholdUtc
                                && p.Candidate.ApplicationDateEnteredUtc >= earliestAnchorUtc)
                            || (p.Reason == PaymentReason.Retest
                                && p.Candidate.ApplicationStatus == CandidateApplicationStatus.Failed
                                && p.Candidate.ResultMarkedUtc != null
                                && p.Candidate.ResultMarkedUtc <= dueThresholdUtc
                                && p.Candidate.ResultMarkedUtc >= earliestAnchorUtc)))
            .ToListAsync(cancellationToken);

        return [.. payments.Select(payment => new MessageSubject(
            payment.Id,
            MessageSubjectType.Payment,
            payment.Candidate.Email,
            new Dictionary<string, string>
            {
                ["CandidateName"] = payment.Candidate.Name ?? "",
                ["SessionDate"] = SessionTimeFormatter.ForCandidate(payment.Candidate.Session.ScheduledStartUtc),
                // Never "C"/InvariantCulture, which renders the generic currency sign rather than a
                // dollar, and never a bare :F2, which follows the ambient culture. See Core/Usd.cs.
                ["PaymentAmount"] = Usd.Format(payment.Amount)
            }))];
    }
}
