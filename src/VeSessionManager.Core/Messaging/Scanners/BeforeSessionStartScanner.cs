using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "The session starts in N hours." Replaces
/// <c>CandidateNotificationService.SendDayBeforeRemindersAsync</c>, whose 24 is now a rule's
/// <c>ParameterHours</c>.
///
/// <para><b>A rolling window between two instants, never a calendar date (#220.)</b> This used to
/// compare against "tomorrow" as a UTC calendar date, which broke twice over. Sessions run in the
/// evening Eastern, and anything from ~8pm ET onward is already tomorrow in raw UTC — so a
/// Monday-evening session is stored on Tuesday, "tomorrow in UTC" is the session's own Eastern day,
/// and the "day before" reminder went out on the day of the session. On top of that the job ticks on
/// an interval from Worker start, so which side of UTC midnight it landed on depended on when the
/// Worker was last deployed: the same session could be reminded anywhere from ~36 hours out to ~3
/// hours out. Comparing two instants removes the whole class — there is no calendar date, so there is
/// no timezone to get wrong — and it is why the rule's parameter is hours.</para>
/// </summary>
public class BeforeSessionStartScanner(AppDbContext dbContext) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.BeforeSessionStart;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var parameterHours = MessageTriggerDefinitions.ParameterHoursOrDefault(rule);
        var windowEndUtc = nowUtc.AddHours(parameterHours);

        // The moment this trigger is about is (start - ParameterHours), so requiring that moment to
        // fall at or after the rule's creation means requiring the start itself to be at least that
        // far past it. This is the guarantee Mike asked for in the issue: add a 7-day rule today and
        // nobody already inside seven days of their session hears from it.
        var earliestStartUtc = rule.CreatedUtc.AddHours(parameterHours);

        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && !settled.Contains(c.Id)
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        // Starts within the window, and has not started yet. The upper bound is what
                        // makes this a reminder rather than a notification about something already
                        // under way; the run marker keeps it to once.
                        && c.Session.ScheduledStartUtc > nowUtc
                        && c.Session.ScheduledStartUtc <= windowEndUtc
                        && c.Session.ScheduledStartUtc >= earliestStartUtc
                        && (onlySessionId == null || c.SessionId == onlySessionId))
            .ToListAsync(cancellationToken);

        return [.. candidates.Select(candidate => new MessageSubject(
            candidate.Id,
            MessageSubjectType.Candidate,
            candidate.Email,
            new Dictionary<string, string>
            {
                ["CandidateName"] = candidate.Name ?? "",
                ["CandidateFirstName"] = candidate.FirstName ?? "",
                ["SessionDate"] = SessionTimeFormatter.ForCandidate(candidate.Session.ScheduledStartUtc),
                ["ZoomJoinUrl"] = candidate.Session.ZoomJoinUrl ?? "",
                ["OutstandingPaymentLinkUrl"] = candidate.Payments
                    .Where(p => p.Status == PaymentStatus.Unpaid && p.PaymentLinkUrl != null)
                    .OrderByDescending(p => p.CreatedUtc)
                    .Select(p => p.PaymentLinkUrl)
                    .FirstOrDefault() ?? ""
            },
            sentUtc => candidate.DayBeforeReminderSentUtc = sentUtc))];
    }
}
