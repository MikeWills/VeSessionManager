using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "This exam fee is still unpaid, and the session it belongs to starts in N hours" (2026-08-25).
///
/// <para>Mike: a candidate who has not paid cannot test, and every existing money trigger anchors on
/// something <i>after</i> the fact — the FCC application, the FCC fee. Nothing warned before the
/// session, while there was still time to do something about it.</para>
///
/// <para><b>Not the old <c>PaymentUnpaid</c> trigger repurposed.</b> That trigger's clock was the FCC
/// application date, which for most candidates does not exist yet before their session — there would
/// be nothing to anchor on. It (and the <c>Payment.ExpiredUnpaid</c> bookkeeping write its hours also
/// drove) is gone entirely as of 2026-08-25 — see <c>PaymentReminderService</c>'s own summary and
/// CLAUDE.md's "No fee, no test" Known Constraint. This scanner anchors on the session's own start
/// time instead, counting backward, exactly like <see cref="BeforeSessionStartScanner"/> — the only
/// difference is the extra "and still unpaid" filter.</para>
///
/// <para><b>No retest branch, unlike the old trigger.</b> That one needed a special case because it
/// anchored on the FCC application/result date, which a retest candidate never has in the normal
/// shape. This scanner anchors on the session itself, and a retest payment belongs to a session like
/// any other — the ordinary query already covers it.</para>
///
/// <para><b>No <c>PaymentEligibilityWindow</c> either.</b> That guard exists to stop the historical
/// import's backfilled candidates being chased about sessions from months ago. This only ever looks at
/// sessions that have <i>not started yet</i>, so a backfilled session — always in the past — can never
/// match in the first place.</para>
/// </summary>
public class PaymentUnpaidBeforeSessionScanner(AppDbContext dbContext) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.PaymentUnpaidBeforeSession;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var parameterHours = MessageTriggerDefinitions.ParameterHoursOrDefault(rule);
        var windowEndUtc = nowUtc.AddHours(parameterHours);

        // Same guarantee as BeforeSessionStartScanner: a rule added today — or re-enabled, or a
        // team's email just configured — never fires for someone already inside its own window at
        // that moment. See MessageRuleEligibility.
        var earliestStartUtc = MessageRuleEligibility.FloorUtc(team, rule).AddHours(parameterHours);

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
                        // Starts within the window, and has not started yet — a reminder, not a
                        // notice about something already under way.
                        && p.Candidate.Session.ScheduledStartUtc > nowUtc
                        && p.Candidate.Session.ScheduledStartUtc <= windowEndUtc
                        && p.Candidate.Session.ScheduledStartUtc >= earliestStartUtc
                        && (onlySessionId == null || p.Candidate.SessionId == onlySessionId))
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
                ["PaymentAmount"] = Usd.Format(payment.Amount),
                ["PaymentLinkUrl"] = payment.PaymentLinkUrl ?? ""
            })
            { SessionLeadCallSign = payment.Candidate.Session.TeamLeadCallSign })];
    }
}
