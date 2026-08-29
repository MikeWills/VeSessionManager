using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Core.Messaging.Scanners;

/// <summary>
/// "The FCC has been waiting N hours for its own application fee." Replaces
/// <c>PaymentReminderService.SendFccFeeRemindersAsync</c>, whose 5 days is now a rule's
/// <c>ParameterHours</c>.
///
/// <para><b>This is FCC's fee, not the team's (#219.)</b> It scans Candidates, not Payments: the fee
/// is paid directly to the FCC through CORES and never passes through this app. The signal is ULS's
/// own <c>FVPOFF</c>, mapped to <see cref="FccApplicationPaymentStatus.PendingVerification"/> by
/// <c>UlsWatcherService</c>. There is deliberately no payment link in the placeholder set — offering
/// the team's Square link here points the reader at a different bill, which was the original defect
/// rather than a fix for it.</para>
///
/// <para><b>Why the anchor is <c>ApplicationDateEnteredUtc</c> and not the session.</b> The fee falls
/// due when the FCC receives the application, not when the exam was sat. Mike's point on the issue is
/// that FCC's "date entered" is often not when they actually got it, which is exactly why the hours
/// are now a rule's to set.</para>
/// </summary>
public class FccFeeOutstandingScanner(AppDbContext dbContext) : IMessageTriggerScanner
{
    public MessageTrigger Trigger => MessageTrigger.FccFeeOutstanding;

    public async Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, DateTime nowUtc, int? onlySessionId, CancellationToken cancellationToken)
    {
        var parameterHours = MessageTriggerDefinitions.ParameterHoursOrDefault(rule);

        // Due: the anchor is at least ParameterHours old.
        var dueThresholdUtc = nowUtc.AddHours(-parameterHours);

        // Not retroactive: the moment (anchor + ParameterHours) must fall at or after the real floor,
        // which is the same as the anchor falling at or after (floor - ParameterHours). See
        // MessageRuleEligibility for what folds into that floor beyond CreatedUtc.
        var earliestAnchorUtc = MessageRuleEligibility.FloorUtc(team, rule).AddHours(-parameterHours);

        var settled = dbContext.MessageRuleRuns
            .Where(r => r.MessageRuleId == rule.Id && MessageRuleOutcomes.Terminal.Contains(r.Outcome))
            .Select(r => r.SubjectId);

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.FccPaymentStatus == FccApplicationPaymentStatus.PendingVerification
                        && !settled.Contains(c.Id)
                        && c.PiiPurgedUtc == null
                        && c.Email != null
                        // FCC's own clock, and the only date that means anything here.
                        && c.ApplicationDateEnteredUtc != null
                        && c.ApplicationDateEnteredUtc <= dueThresholdUtc
                        && c.ApplicationDateEnteredUtc >= earliestAnchorUtc
                        // A terminal candidate has no live application for a fee to be outstanding
                        // on. PendingVerification should already have cleared, but ULS is a mirror
                        // polled twice a day and this costs nothing.
                        && !CandidateApplicationStatusExtensions.TerminalStatuses.Contains(c.ApplicationStatus)
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        // Status == Active means "not cancelled", not "not finished" — without this
                        // exclusion the historical import's backfilled candidates would get emailed
                        // about a fee for a session they sat months ago (#88). Defense in depth: a
                        // historical candidate is realistically already terminal (auto-Granted by
                        // MarkHistoricalCandidatesGranted) and excluded above anyway.
                        && c.Session.ImportedHistoricallyUtc == null
                        && (onlySessionId == null || c.SessionId == onlySessionId))
            .ToListAsync(cancellationToken);

        return [.. candidates.Select(candidate => new MessageSubject(
            candidate.Id,
            MessageSubjectType.Candidate,
            candidate.Email,
            new Dictionary<string, string>
            {
                ["CandidateName"] = candidate.Name ?? "",
                ["SessionDate"] = SessionTimeFormatter.ForCandidate(candidate.Session.ScheduledStartUtc),
                // The FRN is what CORES asks for, so a reminder that omits it sends the reader
                // hunting for it. Public FCC data, not PII — see the FRN note in CLAUDE.md.
                ["Frn"] = candidate.Frn ?? "",
                ["FccApplicationFileNumber"] = candidate.UlsApplicationFileNumber ?? ""
            },
            sentUtc => candidate.FccFeeReminderSentUtc = sentUtc)
            {
                SessionLeadCallSign = candidate.Session.TeamLeadCallSign,
                // No prior license = a first-time applicant; anything else is an upgrade. See
                // FccCandidatePopulation and the FCC-issue switches on SystemSettings.
                FccPopulation = candidate.InitialLicenseClass is null or LicenseClass.None
                    ? FccCandidatePopulation.NewLicense
                    : FccCandidatePopulation.Upgrade
            })];
    }
}
