using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "The FCC has granted a license from this session" (#401 PR3) — the natural home for a
/// getting-started email, and the only trigger where <c>{{CallSign}}</c> resolves to anything.
///
/// <para><b>The moment is FCC's own grant date</b>, <see cref="Candidate.LicenseGrantDateUtc"/>, which
/// is date-only and stamped at UTC midnight by the ULS lookup. So a rule created at 2pm today will not
/// fire for a license FCC dated today — its moment reads as this morning, which is before the rule
/// existed. That is a day's worth of the safe direction, and the alternative was a second column
/// recording when the watcher noticed; the grant date is the honest answer to "when did this become
/// true" and everything after today fires normally.</para>
///
/// <para><b>An upgrade whose license predates the session does not fire</b>, checked in memory
/// because <c>LicenseGrantPredatesSession</c> needs the session loaded. Somebody who walked in already
/// licensed did not get a call sign from this exam, and "congratulations on your new call sign" is
/// wrong for them. Note the related trap already recorded in CLAUDE.md: FCC's grant date does not
/// advance on a class upgrade, so for a genuine upgrade the watcher stores the effective date
/// instead — which is why this check is about the session, not about whether a call sign is new.</para>
/// </summary>
public class LicenseGrantedScanner(AppDbContext dbContext, ILogger<LicenseGrantedScanner> logger) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.LicenseGranted;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var floorUtc = MessageRuleEligibility.FloorUtc(team, rule);
        var granted = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && !settled.Contains(c.Id)
                        && c.ApplicationStatus == CandidateApplicationStatus.Granted
                        // No call sign means nothing to congratulate anybody about, whatever the
                        // status says — and {{CallSign}} is the whole point of this trigger.
                        && c.CallSign != null
                        && c.LicenseGrantDateUtc != null
                        && c.LicenseGrantDateUtc >= floorUtc
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        && (onlySessionId == null || c.SessionId == onlySessionId))
            .ToListAsync(cancellationToken);

        var newlyLicensed = granted.Where(c => !c.LicenseGrantPredatesSession()).ToList();
        var alreadyLicensedCount = granted.Count - newlyLicensed.Count;
        if (alreadyLicensedCount > 0)
        {
            logger.LogInformation("Skipped {Trigger} for {Count} candidate(s) in team {TeamId} whose license predates their session — they did not earn it here",
                Trigger, alreadyLicensedCount, team.Id);
        }

        return [.. newlyLicensed.Select(candidate => new MessageSubject(
            candidate.Id,
            MessageSubjectType.Candidate,
            candidate.Email,
            new Dictionary<string, string>
            {
                ["CandidateName"] = candidate.Name ?? "",
                ["CandidateFirstName"] = candidate.FirstName ?? "",
                ["SessionDate"] = SessionTimeFormatter.ForCandidate(candidate.Session.ScheduledStartUtc),
                ["CallSign"] = candidate.CallSign ?? "",
                // The one trigger whose seeded template (GettingStartedLocally) is about the club
                // rather than the exam, so it signs off with a name.
                ["TeamName"] = team.Name
            })
            { SessionLeadCallSign = candidate.Session.TeamLeadCallSign })];
    }
}
