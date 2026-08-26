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
/// Candidate email that somebody asks for by pressing a button — a resend, the two instruction
/// emails, and the hand-composed send off a session (#144). Rendered through the shared
/// EmailTemplateRenderer and sent through IEmailSender, never with content hardcoded here.
///
/// <para><b>The scan-based half of this class is gone (#401, 2026-08-16.)</b> It used to hold two
/// poll passes as well — SendRegistrationConfirmationsAsync and SendDayBeforeRemindersAsync, each
/// keyed off its own <c>Candidate.…SentUtc</c> column. Those are now
/// <c>MessageTrigger.CandidateRegistered</c> and <c>MessageTrigger.BeforeSessionStart</c>, so which
/// message goes out and how long before is a row a team owns rather than a literal here. See
/// docs/trigger-points.md. The columns are still written, by the dispatcher, because the candidate
/// Email history screen renders them; they are no longer what decides whether to send.</para>
///
/// <para><b>Which is what made the mute check honest.</b> Every method below now refuses a muted team
/// with <see cref="CandidateEmailSendResult.EmailMuted"/> instead of reporting success (#396). It
/// could not do that while the jobs shared TrySendAsync: a job must settle silently when email is
/// switched off, or it builds a backlog to flush on re-enable, and that same "return true, nothing to
/// do" answer reached somebody standing at a button and told them the mail had gone.</para>
///
/// Multi-team: one Team per call — each team has its own SMTP account and its own
/// EmailSettings/EmailTemplate rows. See docs/multi-team.md.
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
    private const string YouthProgramInstructionsKey = "ArrlYouthProgramInstructions";
    private const string FelonyDisclosureInstructionsKey = "FelonyDisclosureInstructions";

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

        if (IsMuted(team, RegistrationConfirmationKey))
        {
            return CandidateEmailSendResult.EmailMuted;
        }

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(
            team, credentials, RegistrationConfirmationKey, candidate, emailSettings, placeholders,
            // The message *is* a registration confirmation however it was set off, so it records that
            // trigger rather than SentByHand — which is also what lets the history tell a resend from
            // the original by its label alone.
            MessageTrigger.CandidateRegistered, ResentConfirmationLabel, cancellationToken))
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

        if (IsMuted(team, YouthProgramInstructionsKey))
        {
            return CandidateEmailSendResult.EmailMuted;
        }

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(
            team, credentials, YouthProgramInstructionsKey, candidate, emailSettings, placeholders,
            // No trigger point sends this one — a person decides — so it is the marker value.
            MessageTrigger.ManualYouthProgramInstructions, YouthProgramLabel, cancellationToken))
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

        if (IsMuted(team, FelonyDisclosureInstructionsKey))
        {
            return CandidateEmailSendResult.EmailMuted;
        }

        var credentials = team.ToEmailCredentials();
        if (!await TrySendAsync(
            team, credentials, FelonyDisclosureInstructionsKey, candidate, emailSettings, placeholders,
            // A rule can send this too (FelonyDisclosureDeclaredScanner), so it records the same
            // trigger either way and the history does not care which path produced it.
            MessageTrigger.ManualFelonyDisclosureInstructions, FelonyInstructionsLabel, cancellationToken))
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
    /// The same hand-composed send as <see cref="SendComposedAsync"/>, scoped by team and the
    /// "still waiting on an FCC grant" population instead of one session (2026-08-26) — the bulk-email
    /// screen off Applicant Status, built for exactly the case a real FCC-wide processing stall
    /// leaves behind: reminders suppressed via <c>SystemSettings.FccIssueActive</c>, and a human who
    /// still wants to tell some or all of those candidates what's going on.
    ///
    /// <para><b>Recipients are re-derived, never trusted from the form</b> — same reasoning as
    /// <see cref="SendComposedAsync"/> (#238), just scoped by team membership and
    /// <see cref="CandidateApplicationStatusExtensions.AwaitingFccGrant"/> instead of one
    /// <c>SessionId</c>. A posted id for a candidate who has since been granted, or who belongs to a
    /// different team, is silently dropped and counted in <see cref="CandidateEmailBatchResult.NotOnSession"/>
    /// rather than failing the whole batch.</para>
    ///
    /// <para><b>One team only, deliberately.</b> Applicant Status can merge every team a
    /// SystemAdmin can see, but a message has to go out through <i>some</i> team's own SMTP
    /// credentials — sending across several teams at once would mean grouping recipients by team
    /// under the hood regardless, and the caller (the page) already requires a single team to be
    /// selected before this is reachable at all, which answers the same question the UI would
    /// otherwise have to ask.</para>
    /// </summary>
    public async Task<CandidateEmailBatchResult> SendComposedToPendingCandidatesAsync(
        int teamId, IReadOnlyList<int> candidateIds, string subject, string body, string templateLabel,
        int userId, CancellationToken cancellationToken)
    {
        var result = new CandidateEmailBatchResult();

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            result.Error = "That team no longer exists.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            result.Error = "An email needs both a subject and a message.";
            return result;
        }

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

        if (!integrationState.ShouldCall(team, TeamIntegration.Email, "sending a composed candidate email"))
        {
            result.Error = "Email is switched off for this team, so nothing was sent. Turn it back on in Team Settings.";
            return result;
        }

        var recipients = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.Session.TeamId == teamId && candidateIds.Contains(c.Id))
            .AwaitingFccGrant()
            .ToListAsync(cancellationToken);

        result.NotOnSession = candidateIds.Distinct().Count() - recipients.Count;
        if (result.NotOnSession > 0)
        {
            logger.LogWarning(
                "Composed candidate email for team {TeamId} requested {Requested} recipient(s), {Dropped} of which are no longer pending an FCC grant on this team and were dropped.",
                teamId, candidateIds.Distinct().Count(), result.NotOnSession);
        }

        var addressable = new List<Candidate>(recipients.Count);
        var messages = new List<EmailMessage>(recipients.Count);

        foreach (var candidate in recipients)
        {
            if (string.IsNullOrWhiteSpace(candidate.Email))
            {
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
                result.Failed++;
                logger.LogError(outcomes[i].Error, "Failed to send a composed email to candidate {CandidateId}", addressable[i].Id);
                continue;
            }

            result.Sent++;
            dbContext.CandidateEmailSends.Add(new CandidateEmailSend
            {
                CandidateId = addressable[i].Id,
                TemplateLabel = templateLabel,
                SentUtc = now,
                SentByUserId = userId
            });
        }

        dbContext.AddAuditLog(userId, "CandidateEmailsSent", nameof(Team), team.Id,
            $"\"{templateLabel}\" to candidates pending an FCC grant on team {team.Name}: {result}", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Composed candidate email for team {TeamId}: {Result}", team.Id, result);
        return result;
    }

    /// <summary>
    /// Refuses a muted team, and says so (#396). Every caller left in this class is somebody standing
    /// at a button, and the answer they need is "nothing was sent" rather than silence.
    ///
    /// <para>The mute check used to live in <see cref="TrySendAsync"/>, where it returned
    /// <c>true</c> — "nothing more to do" — because the poll passes that also went through there must
    /// settle rather than queue. Those passes are rules now (#401), so the compromise has no second
    /// side to serve.</para>
    /// </summary>
    private bool IsMuted(Team team, string templateKey) =>
        !integrationState.ShouldCall(team, TeamIntegration.Email, $"sending {templateKey}");

    /// <summary>
    /// Every templated candidate email this service sends funnels through here — one render, one
    /// send, one place the team's monitoring copy is attached. Takes the Team rather than its id
    /// because <see cref="Team.ToEmailCredentials"/> and the From/Reply-To come off it.
    /// </summary>
    private async Task<bool> TrySendAsync(
        Team team, EmailCredentials credentials, string templateKey, Candidate candidate, EmailSettings emailSettings,
        Dictionary<string, string> placeholders, MessageTrigger trigger, string label, CancellationToken cancellationToken)
    {
        // The message for whatever sends this, rather than a template looked up by key (2026-08-21).
        // The trigger was already being passed for the run marker, so it was the thing identifying
        // this send all along — the key was a second name for the same fact, and the one that could
        // point at nothing.
        //
        // Switched off means not sent: for these, "off" is how a team declines an email it does not
        // want, and there is no scan to stop instead.
        var message = await dbContext.MessageRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == team.Id && r.Trigger == trigger && r.IsEnabled, cancellationToken);
        if (message is null)
        {
            logger.LogInformation("Team {TeamId} has no enabled message for {Trigger} — nothing sent for {Label}", team.Id, trigger, label);
            return false;
        }

        var rendered = await templateRenderer.RenderTextAsync(
            team.Id, message.Subject, message.Body, placeholders, message.Name, cancellationToken);

        await emailSender.SendAsync(
            credentials,
            // Every candidate-facing email this service sends funnels through here, so this is the
            // single place the team's monitoring copy is attached (issue #207).
            new EmailMessage(candidate.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName,
                emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo,
                BccAddress: emailSettings.BccAddress),
            cancellationToken);

        // …and, since #417, the single place a hand-send is recorded. One dispatcher, several kinds
        // of trigger: a button is a perfectly good trigger, it is just not a scheduled one. Without
        // this the send existed only as a Candidate.*SentUtc column, which is what made a candidate's
        // email history need per-column fallbacks and a dedup rule (#415).
        //
        // MessageRuleId is null because no rule produced it — the same nullable column that lets a
        // run outlive a deleted rule. The caller saves; every one of them already does, for the
        // legacy column written beside this.
        dbContext.MessageRuleRuns.Add(new MessageRuleRun
        {
            TeamId = team.Id,
            MessageRuleId = null,
            RuleName = label,
            Trigger = trigger,
            SubjectType = MessageSubjectType.Candidate,
            SubjectId = candidate.Id,
            FiredUtc = timeProvider.GetUtcNow().UtcDateTime,
            Outcome = MessageRuleOutcome.Sent,
            Detail = "Sent by hand"
        });
        return true;
    }

    /// <summary>
    /// What a hand-sent email is called in a candidate's history. Constants rather than literals
    /// because the backfill migration writes the same strings for rows that predate #417, and the two
    /// have to match or one send appears under two names.
    /// </summary>
    public const string ResentConfirmationLabel = "Registration confirmation (resent)";
    public const string YouthProgramLabel = "Youth Program instructions";
    public const string FelonyInstructionsLabel = "Felony disclosure instructions";

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
