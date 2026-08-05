using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Notifications;

/// <summary>
/// Phase 4: sends the two candidate-facing emails via the shared template engine
/// (EmailTemplateRenderer) and SMTP sender (IEmailSender). Every later phase that sends email
/// should follow this same shape — render via EmailTemplateRenderer using its own EmailTemplate
/// Key, never hardcode content — per the spec's explicit note to keep this pattern consistent.
///
/// Both methods are scan-based and idempotent, like Phase 2/3: a Candidate's
/// RegistrationConfirmationSentUtc/DayBeforeReminderSentUtc being null is the "needs to be sent"
/// signal, set only after a successful send, so a mid-run crash or per-item failure retries
/// cleanly next run without resending anything already delivered.
///
/// Multi-team: this service now operates on one Team's candidates per call — each team has its
/// own separate SMTP account and its own EmailSettings/EmailTemplate rows (confirmed with the
/// user — content is per-team customizable, not shared). See docs/multi-team.md.
/// </summary>
public class CandidateNotificationService(
    AppDbContext dbContext,
    EmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    IOptions<AppOptions> appOptions,
    ILogger<CandidateNotificationService> logger)
{
    private const string RegistrationConfirmationKey = "RegistrationConfirmation";
    private const string DayBeforeReminderKey = "DayBeforeReminder";
    private const string YouthProgramInstructionsKey = "ArrlYouthProgramInstructions";
    private const string FelonyDisclosureInstructionsKey = "FelonyDisclosureInstructions";

    /// <param name="onlySessionId">Restrict the run to one session's candidates (the Detail page's
    /// session-scoped refresh); null (every scheduled/team-wide run) scans the whole team.</param>
    public async Task<EmailNotificationResult> SendRegistrationConfirmationsAsync(Team team, CancellationToken cancellationToken, int? onlySessionId = null)
    {
        var result = new EmailNotificationResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var recentSessionCutoff = now.AddDays(-1);

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            logger.LogWarning("No EmailSettings row exists yet for team {TeamId} — skipping registration confirmations until seeded", team.Id);
            return result;
        }

        var candidatesIncludingPastSessions = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && c.RegistrationConfirmationSentUtc == null
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
        var candidates = candidatesIncludingPastSessions.Where(c => !c.Session.HasEnded(now)).ToList();
        var skippedPastSessionCount = candidatesIncludingPastSessions.Count - candidates.Count;
        if (skippedPastSessionCount > 0)
        {
            logger.LogInformation("Skipped RegistrationConfirmation for {Count} candidate(s) in team {TeamId} whose session has already ended — likely backfilled via the completed-session ingestion window",
                skippedPastSessionCount, team.Id);
        }

        if (candidates.Count > 0 && !team.IsEmailConfigured)
        {
            // SMTP is optional the same way Square is (see PaymentGenerationService) — skip
            // quietly rather than retry-and-fail-log every poll; RegistrationConfirmationSentUtc
            // stays null, so the very next poll sends everything backlogged once SMTP is set up.
            logger.LogInformation("SMTP is not fully configured for team {TeamId} — {PendingCount} registration confirmation(s) waiting; will send automatically once configured",
                team.Id, candidates.Count);
            return result;
        }

        var credentials = team.ToEmailCredentials();

        foreach (var candidate in candidates)
        {
            try
            {
                var paymentLinkUrl = candidate.Session.FeeConfiguration.FeeCollectionEnabled
                    ? candidate.Payments.FirstOrDefault(p => p.Reason == PaymentReason.InitialExam)?.PaymentLinkUrl ?? ""
                    : "";

                var placeholders = new Dictionary<string, string>
                {
                    ["CandidateName"] = candidate.Name ?? "",
                    ["CandidateFirstName"] = candidate.FirstName ?? "",
                    ["SessionDate"] = FormatSessionDate(candidate.Session.ScheduledStartUtc),
                    ["ZoomJoinUrl"] = candidate.Session.ZoomJoinUrl ?? "",
                    ["PaymentLinkUrl"] = paymentLinkUrl,
                    ["YouthPaymentLinkUrl"] = BuildYouthPaymentLinkUrl(candidate),
                    ["PrivacyPolicyUrl"] = emailSettings.PrivacyPolicyUrl
                };

                if (!await TrySendAsync(team.Id, credentials, RegistrationConfirmationKey, candidate, emailSettings, placeholders, cancellationToken))
                {
                    result.Failed++;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                candidate.RegistrationConfirmationSentUtc = now;
                result.Sent++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                logger.LogError(ex, "Failed to send RegistrationConfirmation for candidate {CandidateId}", candidate.Id);
            }

            // Save after every candidate so a crash mid-run, or one send failing, never loses
            // progress already made on others, and never resends to someone already notified.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Registration confirmations finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    public async Task<EmailNotificationResult> SendDayBeforeRemindersAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new EmailNotificationResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // "Tomorrow" evaluated as a UTC calendar date, consistent with every other date stored in
        // this app — sessions occurring right around UTC midnight may read as "tomorrow" a little
        // earlier/later than the Session Manager's own local calendar day.
        var tomorrowStartUtc = now.Date.AddDays(1);
        var tomorrowEndUtc = tomorrowStartUtc.AddDays(1);

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            logger.LogWarning("No EmailSettings row exists yet for team {TeamId} — skipping day-before reminders until seeded", team.Id);
            return result;
        }

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Email != null
                        && c.DayBeforeReminderSentUtc == null
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        && c.Session.ScheduledStartUtc >= tomorrowStartUtc
                        && c.Session.ScheduledStartUtc < tomorrowEndUtc)
            .ToListAsync(cancellationToken);

        if (candidates.Count > 0 && !team.IsEmailConfigured)
        {
            logger.LogInformation("SMTP is not fully configured for team {TeamId} — {PendingCount} day-before reminder(s) waiting; will send automatically once configured",
                team.Id, candidates.Count);
            return result;
        }

        var credentials = team.ToEmailCredentials();

        foreach (var candidate in candidates)
        {
            try
            {
                var outstandingPaymentLinkUrl = candidate.Payments
                    .Where(p => p.Status == PaymentStatus.Unpaid && p.PaymentLinkUrl != null)
                    .OrderByDescending(p => p.CreatedUtc)
                    .Select(p => p.PaymentLinkUrl)
                    .FirstOrDefault() ?? "";

                var placeholders = new Dictionary<string, string>
                {
                    ["CandidateName"] = candidate.Name ?? "",
                    ["CandidateFirstName"] = candidate.FirstName ?? "",
                    ["SessionDate"] = FormatSessionDate(candidate.Session.ScheduledStartUtc),
                    ["ZoomJoinUrl"] = candidate.Session.ZoomJoinUrl ?? "",
                    ["OutstandingPaymentLinkUrl"] = outstandingPaymentLinkUrl
                };

                if (!await TrySendAsync(team.Id, credentials, DayBeforeReminderKey, candidate, emailSettings, placeholders, cancellationToken))
                {
                    result.Failed++;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                candidate.DayBeforeReminderSentUtc = now;
                result.Sent++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                logger.LogError(ex, "Failed to send DayBeforeReminder for candidate {CandidateId}", candidate.Id);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Day-before reminders finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Phase 9b's "resend reminder email" action — re-renders and re-sends RegistrationConfirmation
    /// on demand, regardless of RegistrationConfirmationSentUtc, and refreshes that timestamp.
    /// Unlike the scan-based Send*Async methods above, this is a single-candidate, immediately-
    /// triggered call from the admin UI, not a poll pass.
    /// </summary>
    public async Task<CandidateEmailSendResult> ResendRegistrationConfirmationAsync(int candidateId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.Team)
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Include(c => c.Payments)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateEmailSendResult.CandidateNotFound;
        }

        if (string.IsNullOrWhiteSpace(candidate.Email))
        {
            return CandidateEmailSendResult.NoEmailAddress;
        }

        var team = candidate.Session.Team;
        if (!team.IsEmailConfigured)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var paymentLinkUrl = candidate.Session.FeeConfiguration.FeeCollectionEnabled
            ? candidate.Payments.FirstOrDefault(p => p.Reason == PaymentReason.InitialExam)?.PaymentLinkUrl ?? ""
            : "";

        var placeholders = new Dictionary<string, string>
        {
            ["CandidateName"] = candidate.Name ?? "",
            ["CandidateFirstName"] = candidate.FirstName ?? "",
            ["SessionDate"] = FormatSessionDate(candidate.Session.ScheduledStartUtc),
            ["ZoomJoinUrl"] = candidate.Session.ZoomJoinUrl ?? "",
            ["PaymentLinkUrl"] = paymentLinkUrl,
            ["YouthPaymentLinkUrl"] = BuildYouthPaymentLinkUrl(candidate),
            ["PrivacyPolicyUrl"] = emailSettings.PrivacyPolicyUrl
        };

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(team.Id, credentials, RegistrationConfirmationKey, candidate, emailSettings, placeholders, cancellationToken))
        {
            return CandidateEmailSendResult.TemplateMissing;
        }

        candidate.RegistrationConfirmationSentUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Resent RegistrationConfirmation for candidate {CandidateId}", candidate.Id);
        return CandidateEmailSendResult.Sent;
    }

    /// <summary>
    /// Phase 9b's "Send ARRL Youth Program instructions" row action — only meaningful when the
    /// candidate's session is under a Vec with SupportsYouthProgram (the caller/UI should already
    /// gate the button's visibility on that, but this is checked again here since it's the actual
    /// authority, not just a UI nicety).
    /// </summary>
    public async Task<CandidateEmailSendResult> SendYouthProgramInstructionsAsync(int candidateId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.Team)
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateEmailSendResult.CandidateNotFound;
        }

        if (!candidate.Session.Vec.SupportsYouthProgram)
        {
            return CandidateEmailSendResult.VecDoesNotSupportYouthProgram;
        }

        if (string.IsNullOrWhiteSpace(candidate.Email))
        {
            return CandidateEmailSendResult.NoEmailAddress;
        }

        var team = candidate.Session.Team;
        if (!team.IsEmailConfigured)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var placeholders = new Dictionary<string, string>
        {
            ["CandidateName"] = candidate.Name ?? "",
            ["CallSign"] = candidate.CallSign ?? ""
        };

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(team.Id, credentials, YouthProgramInstructionsKey, candidate, emailSettings, placeholders, cancellationToken))
        {
            return CandidateEmailSendResult.TemplateMissing;
        }

        candidate.YouthProgramInstructionsSentUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sent ArrlYouthProgramInstructions for candidate {CandidateId}", candidate.Id);
        return CandidateEmailSendResult.Sent;
    }

    /// <summary>
    /// Sent automatically (not a standalone button) by SessionActionService.MarkCompletedAsync for
    /// each candidate whose Tested flag just flipped to true as part of that action and who has
    /// HasFelonyDisclosure = true — tells them special FCC steps are required, nothing more.
    /// </summary>
    public async Task<CandidateEmailSendResult> SendFelonyDisclosureInstructionsAsync(int candidateId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.Team)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateEmailSendResult.CandidateNotFound;
        }

        if (string.IsNullOrWhiteSpace(candidate.Email))
        {
            return CandidateEmailSendResult.NoEmailAddress;
        }

        var team = candidate.Session.Team;
        if (!team.IsEmailConfigured)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            return CandidateEmailSendResult.EmailNotConfigured;
        }

        var placeholders = new Dictionary<string, string>
        {
            ["CandidateName"] = candidate.Name ?? ""
        };

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(team.Id, credentials, FelonyDisclosureInstructionsKey, candidate, emailSettings, placeholders, cancellationToken))
        {
            return CandidateEmailSendResult.TemplateMissing;
        }

        candidate.FelonyDisclosureInstructionsSentUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sent FelonyDisclosureInstructions for candidate {CandidateId}", candidate.Id);
        return CandidateEmailSendResult.Sent;
    }

    private async Task<bool> TrySendAsync(
        int teamId, EmailCredentials credentials, string templateKey, Candidate candidate, EmailSettings emailSettings,
        Dictionary<string, string> placeholders, CancellationToken cancellationToken)
    {
        var rendered = await templateRenderer.RenderAsync(teamId, templateKey, placeholders, cancellationToken);
        if (rendered is null)
        {
            return false;
        }

        await emailSender.SendAsync(
            credentials,
            new EmailMessage(candidate.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName, emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo),
            cancellationToken);
        return true;
    }

    private static string FormatSessionDate(DateTime scheduledStartUtc) =>
        scheduledStartUtc.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture) + " UTC";

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
