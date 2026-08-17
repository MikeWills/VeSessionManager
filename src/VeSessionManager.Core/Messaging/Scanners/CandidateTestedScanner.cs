using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "This candidate has sat their exam" (#401 PR3) — new, and nothing sent here before.
///
/// <para><b>The moment is <see cref="Candidate.TestedUtc"/>, a column added for this.</b>
/// <c>Tested</c> is a bool set from three different places, so before PR3 there was no answer to
/// "when did this become true" — and without one, a rule could not be bounded by its own creation and
/// would reach a year of imported history on its first tick. Candidates who tested before that column
/// existed hold null and are therefore never returned, which is the intended behaviour rather than a
/// gap.</para>
///
/// <para><b>It says nothing about passing.</b> <c>Tested</c> means the exam was sat; the result often
/// is not known for hours or days, and a candidate marked tested by "mark session completed" may not
/// even have a graded result yet. A team wanting "congratulations" wants
/// <see cref="MessageTrigger.LicenseGranted"/>.</para>
/// </summary>
public class CandidateTestedScanner(AppDbContext dbContext) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.CandidateTested;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && !settled.Contains(c.Id)
                        && c.Tested
                        // The moment, and the bound. Null means "tested before this column existed",
                        // which excludes every backfilled candidate without needing an age window.
                        && c.TestedUtc != null
                        && c.TestedUtc >= rule.CreatedUtc
                        // A withdrawn candidate is ApplicationStatus.NotTested and never actually sat
                        // anything, whatever a bulk "mark session completed" left on the row.
                        && c.ApplicationStatus != CandidateApplicationStatus.NotTested
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
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
                // Almost always blank here, and correctly so: the FCC has not issued anything yet.
                // Offered because a team may write "your call sign, once it arrives, will be…" and a
                // token that silently does not exist is worse than one that renders empty.
                ["CallSign"] = candidate.CallSign ?? ""
            }))];
    }
}
