using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "A candidate has registered." Replaces
/// <c>CandidateNotificationService.SendRegistrationConfirmationsAsync</c>'s query, guards and
/// placeholders unchanged — PR1 froze behaviour, and every filter below is here because that method
/// had it.
/// </summary>
public class CandidateRegisteredScanner(
    AppDbContext dbContext,
    IOptions<AppOptions> appOptions,
    ILogger<CandidateRegisteredScanner> logger) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.CandidateRegistered;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var recentSessionCutoff = nowUtc.AddDays(-1);

        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var candidatesIncludingPastSessions = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && !settled.Contains(c.Id)
                        // The moment this trigger is about. Bounded by the rule's own creation, so a
                        // rule added today never fires for somebody who registered last week — see
                        // MessageRule.CreatedUtc.
                        && c.DateRegisteredUtc >= rule.CreatedUtc
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        // Query-side coarse bound so a year of backfilled sessions doesn't get
                        // loaded, filtered and log-counted on every tick, forever — the numbers only
                        // ever grow, and the lines drowned out real ones (~1991 for one team). A
                        // session starting more than a day ago has certainly ended (durations are
                        // hours), so this never hides one the precise HasEnded check below needs.
                        && c.Session.ScheduledStartUtc >= recentSessionCutoff
                        && (onlySessionId == null || c.SessionId == onlySessionId))
            .ToListAsync(cancellationToken);

        // A candidate on a session ingested via the completed-session backfill window (see
        // SessionIngestionService) already had their session happen — a "you're registered!" email
        // for something already over would just confuse them. Skipped permanently, not retried:
        // there's no future poll where this session stops being in the past.
        var candidates = candidatesIncludingPastSessions.Where(c => !c.Session.HasEnded(nowUtc)).ToList();
        var skippedPastSessionCount = candidatesIncludingPastSessions.Count - candidates.Count;
        if (skippedPastSessionCount > 0)
        {
            logger.LogInformation("Skipped {Trigger} for {Count} candidate(s) in team {TeamId} whose session has already ended — likely backfilled via the completed-session ingestion window",
                Trigger, skippedPastSessionCount, team.Id);
        }

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
                ["PaymentLinkUrl"] = candidate.Session.FeeConfiguration.FeeCollectionEnabled
                    ? candidate.Payments.FirstOrDefault(p => p.Reason == PaymentReason.InitialExam)?.PaymentLinkUrl ?? ""
                    : "",
                ["YouthPaymentLinkUrl"] = BuildYouthPaymentLinkUrl(candidate),
                ["PrivacyPolicyUrl"] = emailSettings.PrivacyPolicyUrl
            },
            sentUtc => candidate.RegistrationConfirmationSentUtc = sentUtc)
            { SessionLeadCallSign = candidate.Session.TeamLeadCallSign })];
    }

    /// <summary>Blank when the session's Vec doesn't support the youth program, or the InitialExam
    /// Payment has no token (fee collection disabled) — a Team's template copy for a
    /// non-youth-program session just renders a blank line for this token, since no
    /// conditional-block templating exists here to hide it automatically.</summary>
    private string BuildYouthPaymentLinkUrl(Candidate candidate)
    {
        if (!candidate.Session.Vec.SupportsYouthProgram)
        {
            return "";
        }

        var token = candidate.Payments.FirstOrDefault(p => p.Reason == PaymentReason.InitialExam)?.YouthConfirmationToken;
        return token is { } t ? $"{appOptions.Value.PublicBaseUrl}/youth-confirm/{t}" : "";
    }
}
