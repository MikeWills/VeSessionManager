using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;

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
    TeamIntegrationState integrationState,
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

                if (!await TrySendAsync(
            team, credentials, RegistrationConfirmationKey, candidate, emailSettings, placeholders, cancellationToken))
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

    /// <summary>
    /// How far ahead of a session its reminder goes out. A duration rather than a time of day on
    /// purpose — see the window comment below.
    /// </summary>
    public const int PreSessionReminderHours = 24;

    public async Task<EmailNotificationResult> SendDayBeforeRemindersAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new EmailNotificationResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // A rolling 24-hour window ending at the session start — NOT a calendar date (#220).
        //
        // This used to compare against "tomorrow" as a UTC calendar date, which broke in two ways at
        // once. Sessions run in the evening Eastern, and anything from ~8pm ET onward is already
        // tomorrow in raw UTC — so a Monday-evening session is stored on Tuesday, "tomorrow in UTC"
        // is the session's own Eastern day, and the "day before" reminder went out on the day of the
        // session. On top of that the job ticks on an interval from Worker start, so which side of
        // UTC midnight it landed depended on when the Worker was last deployed: the same session
        // could be reminded anywhere from ~36 hours out to ~3 hours out.
        //
        // Comparing two instants removes the whole class: there is no calendar date, so there is no
        // timezone to get wrong, and a restart shifts a reminder by minutes rather than a day. Drift
        // of one tick is acceptable and was Mike's explicit call — "24 hours before the test, no
        // matter the time zone, +/- job run time is fine".
        var windowEndUtc = now.AddHours(PreSessionReminderHours);

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
                        // Starts within the next 24 hours, and has not started yet. The upper bound
                        // is what makes this a reminder rather than a notification about something
                        // already under way; DayBeforeReminderSentUtc keeps it to once.
                        && c.Session.ScheduledStartUtc > now
                        && c.Session.ScheduledStartUtc <= windowEndUtc)
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

                if (!await TrySendAsync(
            team, credentials, DayBeforeReminderKey, candidate, emailSettings, placeholders, cancellationToken))
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
        if (!await TrySendAsync(
            team, credentials, RegistrationConfirmationKey, candidate, emailSettings, placeholders, cancellationToken))
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
        if (!await TrySendAsync(
            team, credentials, YouthProgramInstructionsKey, candidate, emailSettings, placeholders, cancellationToken))
        {
            return CandidateEmailSendResult.TemplateMissing;
        }

        candidate.YouthProgramInstructionsSentUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sent ArrlYouthProgramInstructions for candidate {CandidateId}", candidate.Id);
        return CandidateEmailSendResult.Sent;
    }

    /// <summary>
    /// Tells a candidate who declared a felony disclosure that the FCC requires extra steps of them.
    /// Informational only — the club has no role beyond saying so.
    ///
    /// <para><b>Manual since #221 (2026-08-11), and no longer gated on having tested.</b> It used to
    /// be sent automatically by SessionActionService.MarkCompletedAsync, which meant it always
    /// arrived <i>after</i> the session — the point at which the candidate can no longer easily ask
    /// anyone about it. The useful time to send it is before, while there is still someone to ask, so
    /// the condition is simply that a disclosure was declared.</para>
    ///
    /// <para><b>The disclosure check moved in here with the button.</b> While this was called from
    /// one place it could trust its caller to have filtered; now the id arrives from a form, and
    /// telling the wrong person that their felony disclosure needs FCC paperwork is not an error to
    /// leave to the UI. The page hides the action, and this refuses it — see
    /// CandidateEmailSendResult.NoFelonyDisclosure.</para>
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

        // Checked here, not just in the page. See the note above: the id comes from a form now.
        if (candidate.HasFelonyDisclosure != true)
        {
            return CandidateEmailSendResult.NoFelonyDisclosure;
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
        if (!await TrySendAsync(
            team, credentials, FelonyDisclosureInstructionsKey, candidate, emailSettings, placeholders, cancellationToken))
        {
            return CandidateEmailSendResult.TemplateMissing;
        }

        candidate.FelonyDisclosureInstructionsSentUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sent FelonyDisclosureInstructions for candidate {CandidateId}", candidate.Id);
        return CandidateEmailSendResult.Sent;
    }

    /// <summary>
    /// Sends a <b>hand-composed</b> message to candidates chosen on one session (#144) — the Email
    /// candidates screen. The draft starts from a template and is edited before it goes, so unlike
    /// every other method here the message is not a stored template and there is no
    /// <c>...SentUtc</c> column to guard it: a re-send is a decision somebody made.
    ///
    /// <para><b>It takes no template key on purpose.</b> The draft is the message; the key survives
    /// only as <paramref name="templateLabel"/>, for the history row and the audit line. That is what
    /// makes a blank draft, a shipped template and a team's own template one code path.</para>
    ///
    /// <para><b>Reported like a fan-out, not like a single send.</b> A partial outcome is the normal
    /// case — some candidates have no address — so this returns counts and an optional
    /// <c>Error</c> rather than one enum, exactly as <c>VeSessionInvitationService</c> does for the
    /// same shape of screen.</para>
    /// </summary>
    /// <param name="candidateIds">Whatever the form posted. Re-scoped to the session below; ids outside it are dropped and counted, never mailed.</param>
    public async Task<CandidateEmailBatchResult> SendComposedAsync(
        int sessionId, IReadOnlyList<int> candidateIds, string subject, string body, string templateLabel,
        int userId, CancellationToken cancellationToken)
    {
        var result = new CandidateEmailBatchResult();

        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            result.Error = "That session no longer exists.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            result.Error = "An email needs both a subject and a message.";
            return result;
        }

        var team = session.Team;
        if (!team.IsEmailConfigured)
        {
            result.Error = "This team has no SMTP settings, so nothing can be sent. Set them in Team Settings.";
            return result;
        }

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == team.Id, cancellationToken);
        if (emailSettings is null)
        {
            result.Error = "This team has no email From/Reply-To settings yet, so nothing can be sent.";
            return result;
        }

        // Reported rather than settled silently. TrySendAsync answers true for a muted team — the
        // deliberate settle-without-doing rule that stops the scan-based jobs building a backlog they
        // would then flush all at once — and that is exactly wrong for somebody standing at a button
        // waiting to hear what happened. Same call VeSessionInvitationService.SendAsync makes.
        if (!integrationState.ShouldCall(team, TeamIntegration.Email, "sending a composed candidate email"))
        {
            result.Error = "Email is switched off for this team, so nothing was sent. Turn it back on in Team Settings.";
            return result;
        }

        // Scoped to the session the screen was opened on (#238). The ids arrive from a posted form,
        // so "the screen only offered this session's candidates" is a default, not a constraint —
        // unscoped, this sends an attacker-authored subject and body from the team's own SMTP to any
        // candidate row on the deployment, and the mail is indistinguishable from a genuine one
        // because it *is* genuine: same From, same Reply-To, same server.
        //
        // Ids outside the scope are dropped and counted rather than failing the send: a legitimate
        // sender reaches this by leaving the screen open while a candidate is withdrawn, which should
        // not lose the other nine emails.
        var recipients = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.SessionId == sessionId && candidateIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        result.NotOnSession = candidateIds.Distinct().Count() - recipients.Count;
        if (result.NotOnSession > 0)
        {
            logger.LogWarning(
                "Composed candidate email for session {SessionId} requested {Requested} recipient(s), {Dropped} of which are not on it and were dropped.",
                sessionId, candidateIds.Distinct().Count(), result.NotOnSession);
        }

        // Composed first, sent second (#293): SmtpEmailSender does a full connect + TLS + AUTH +
        // disconnect per message, so a 20-candidate session would otherwise be 20 handshakes inside
        // one POST with the sender watching a spinner.
        var addressable = new List<Candidate>(recipients.Count);
        var messages = new List<EmailMessage>(recipients.Count);

        foreach (var candidate in recipients)
        {
            if (string.IsNullOrWhiteSpace(candidate.Email))
            {
                // Counted, not silently dropped — "sent 8 of 10" with no explanation is worse than a
                // number somebody can act on by filling in an address.
                result.NoEmailAddress++;
                continue;
            }

            var rendered = await templateRenderer.RenderTextAsync(
                team.Id, subject, body, CandidatePlaceholderValues.For(candidate, team.Name), templateLabel, cancellationToken);

            addressable.Add(candidate);
            messages.Add(new EmailMessage(
                candidate.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName,
                emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo,
                BccAddress: emailSettings.BccAddress));
        }

        var outcomes = await emailSender.SendManyAsync(team.ToEmailCredentials(), messages, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        for (var i = 0; i < outcomes.Count; i++)
        {
            if (!outcomes[i].Sent)
            {
                // One bad address must not stop the rest; that rule lives in the sender, which is the
                // only layer that can hold the connection open across the failure.
                result.Failed++;
                logger.LogError(outcomes[i].Error, "Failed to send a composed email to candidate {CandidateId}", addressable[i].Id);
                continue;
            }

            result.Sent++;
            // Only for a delivery that succeeded. This list answers "who has already had one", and a
            // second pass over a session skips the people on it — so a failed send recorded here
            // would hide exactly the person that pass exists to catch.
            dbContext.CandidateEmailSends.Add(new CandidateEmailSend
            {
                CandidateId = addressable[i].Id,
                TemplateLabel = templateLabel,
                SentUtc = now,
                SentByUserId = userId
            });
        }

        dbContext.AddAuditLog(userId, "CandidateEmailsSent", nameof(Session), session.Id,
            $"\"{templateLabel}\" to candidates on session {session.ExamToolsSessionId}: {result}", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Composed candidate email for session {SessionId}: {Result}", session.Id, result);
        return result;
    }

    /// <summary>
    /// Every candidate-facing email this service sends funnels through here, which is why the mute
    /// switch is checked here and not at five call sites (#64). Takes the Team rather than its id for
    /// exactly that reason.
    /// </summary>
    private async Task<bool> TrySendAsync(
        Team team, EmailCredentials credentials, string templateKey, Candidate candidate, EmailSettings emailSettings,
        Dictionary<string, string> placeholders, CancellationToken cancellationToken)
    {
        // Reported as "template missing" is wrong here, so callers get false only after a real
        // failure — a muted send returns true, meaning "nothing more to do", and the caller stamps
        // its ...SentUtc. That is the settle-without-doing-it rule: no retry, no backlog on
        // re-enable. It is safe precisely because it is deliberate and indefinite, unlike an
        // unconfigured integration, which must stay pending so it backfills.
        if (!integrationState.ShouldCall(team, TeamIntegration.Email, $"sending {templateKey}"))
        {
            return true;
        }

        var rendered = await templateRenderer.RenderAsync(team.Id, templateKey, placeholders, cancellationToken);
        if (rendered is null)
        {
            return false;
        }

        await emailSender.SendAsync(
            credentials,
            // Every candidate-facing email this service sends funnels through here, so this is the
            // single place the team's monitoring copy is attached (issue #207).
            new EmailMessage(candidate.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName,
                emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo,
                BccAddress: emailSettings.BccAddress),
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Delegates to SessionTimeFormatter so both services — and both halves of what a candidate
    /// receives — cannot disagree about the wording. This used to be a byte-identical copy in each
    /// file, rendering UTC.
    /// </summary>
    private static string FormatSessionDate(DateTime scheduledStartUtc) =>
        SessionTimeFormatter.ForCandidate(scheduledStartUtc);

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
