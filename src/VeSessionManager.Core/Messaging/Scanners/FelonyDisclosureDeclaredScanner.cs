using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "This candidate declared a felony disclosure" (#401 PR3) — informational, telling them the FCC
/// requires an explanation before a license can be granted.
///
/// <para><b>Declaration, not completion, and that is the whole point.</b> #221 took this email off an
/// automatic path because it fired from "mark session completed", so it could only ever arrive
/// <i>after</i> the exam — when the candidate can no longer easily ask anyone about it. The useful
/// time to send it is before, while there is still someone to ask. Offering it as a trigger fixes the
/// timing rather than reinstating the mistake: <see cref="Candidate.Tested"/> is not consulted at
/// all.</para>
///
/// <para><b>No rule is seeded for it</b>, so nothing changes for any existing team. A team that wants
/// it automatic says so; the per-candidate button stays either way.</para>
///
/// <para>The moment is registration, because the disclosure arrives with the application — there is
/// no separate "declared" timestamp, and the answer does not change afterwards.</para>
/// </summary>
public class FelonyDisclosureDeclaredScanner(AppDbContext dbContext, ILogger<FelonyDisclosureDeclaredScanner> logger) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.FelonyDisclosureDeclared;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var recentSessionCutoff = nowUtc.AddDays(-1);

        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var candidatesIncludingPastSessions = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && !settled.Contains(c.Id)
                        // Nullable, and only true counts: null means ExamTools told us nothing, which
                        // is not the same as "no". Telling the wrong person their felony disclosure
                        // needs FCC paperwork is the mistake this guards, exactly as the button's own
                        // handler does.
                        && c.HasFelonyDisclosure == true
                        && c.DateRegisteredUtc >= rule.CreatedUtc
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        // Same recent-session bound as the registration confirmation, and for the same
                        // reason: this is pre-session advice, so a session already over has nothing
                        // left to advise about.
                        && c.Session.ScheduledStartUtc >= recentSessionCutoff
                        && (onlySessionId == null || c.SessionId == onlySessionId))
            .ToListAsync(cancellationToken);

        var candidates = candidatesIncludingPastSessions.Where(c => !c.Session.HasEnded(nowUtc)).ToList();
        var skippedPastSessionCount = candidatesIncludingPastSessions.Count - candidates.Count;
        if (skippedPastSessionCount > 0)
        {
            logger.LogInformation("Skipped {Trigger} for {Count} candidate(s) in team {TeamId} whose session has already ended — the advice is only useful beforehand",
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
                ["SessionDate"] = SessionTimeFormatter.ForCandidate(candidate.Session.ScheduledStartUtc)
            },
            sentUtc => candidate.FelonyDisclosureInstructionsSentUtc = sentUtc)
            { SessionLeadCallSign = candidate.Session.TeamLeadCallSign })];
    }
}
