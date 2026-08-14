using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Inviting a team's VEs to work an upcoming session (issue #142 phase 6).
///
/// <para><b>The point is the Zoom link.</b> The issue is explicit: the reason this exists is so
/// nobody has to go hunting for it. <c>{{ZoomJoinUrl}}</c> is therefore a first-class placeholder,
/// and the compose screen warns when the session has none yet rather than silently sending an
/// invitation with a blank where the link should be.</para>
///
/// <para>Sent from the <b>team's</b> SMTP, unlike the self-service links which use the deployment
/// sender. This is a team writing to its own volunteers about its own session, so it should come
/// from them — and unlike a VE, a session belongs to exactly one team, so there is no ambiguity to
/// resolve.</para>
///
/// <para>Ad-hoc text rather than a stored template: the issue asks for a way to <i>draft</i> subject
/// and body per send, and an invitation is a different sentence every time. Placeholders use the same
/// <c>{{Token}}</c> convention as the templates so nobody has to learn two syntaxes.</para>
/// </summary>
public class VeSessionInvitationService(
    AppDbContext dbContext,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<VeSessionInvitationService> logger)
{
    /// <summary>What the compose screen offers as insertable chips, and the only tokens substituted below.</summary>
    public static readonly IReadOnlyList<string> Placeholders =
        ["VeName", "CallSign", "SessionTitle", "SessionDate", "ZoomJoinUrl", "TeamName"];

    /// <summary>
    /// Who could be invited: every VE with an active membership on the session's team, annotated with
    /// what would stop them being reachable or eligible.
    /// </summary>
    public async Task<IReadOnlyList<VeInvitationCandidate>> GetCandidatesAsync(int sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return [];
        }

        var memberships = await dbContext.VeTeamMemberships
            .Include(m => m.VolunteerExaminer).ThenInclude(v => v.VecAccreditations)
            .Include(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .Where(m => m.TeamId == session.TeamId && m.IsActive)
            .ToListAsync(cancellationToken);

        var onRoster = await dbContext.SessionVolunteerExaminers
            .Where(l => l.SessionId == sessionId)
            .Select(l => l.VolunteerExaminerId)
            .ToListAsync(cancellationToken);

        return [.. memberships
            .Select(m => new VeInvitationCandidate(
                m.VolunteerExaminer,
                [.. m.TagAssignments.Select(a => a.VeTag.Name).OrderBy(n => n)],
                onRoster.Contains(m.VolunteerExaminerId),
                // The phase 3 eligibility check, surfaced at the moment it is most useful: inviting
                // someone who cannot legally serve on the day is a wasted seat and an awkward
                // conversation later.
                VeSessionEligibility.For(m.VolunteerExaminer, session.ScheduledStartUtc, session.VecId)))
            .OrderBy(c => c.VolunteerExaminer.Name)];
    }

    public async Task<VeInvitationResult> SendAsync(
        int sessionId, IReadOnlyList<int> volunteerExaminerIds, string subject, string body, int userId, CancellationToken cancellationToken)
    {
        var result = new VeInvitationResult();

        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            result.Error = "That session no longer exists.";
            return result;
        }

        if (!session.Team.IsEmailConfigured)
        {
            // The optional-integration convention: say so plainly rather than throwing a MailKit
            // authentication error per recipient.
            result.Error = "This team has no SMTP settings, so invitations cannot be sent. Set them in Team Settings.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            result.Error = "An invitation needs both a subject and a message.";
            return result;
        }

        // Scoped through VeTeamMemberships on the session's own team, matching GetCandidatesAsync's
        // filter exactly (#238). The ids arrive from the posted form, so "the compose screen only
        // offered team members" is not a constraint — it is a default that a hand-made POST ignores.
        //
        // Unscoped, this sent an attacker-authored subject and body from the team's own SMTP
        // credentials to any VolunteerExaminer row on the deployment: other teams' rosters, retired
        // members, anyone. The mail is indistinguishable from a genuine invitation because it *is*
        // genuine — same From, same Reply-To, same server.
        //
        // Ids that fall outside the scope are dropped rather than rejected, and counted below. A
        // legitimate sender can hit this by having the compose screen open while a membership is
        // deactivated, which should not fail the whole send; anyone reaching it by tampering learns
        // only a number they already knew.
        var recipients = await dbContext.VeTeamMemberships
            .Where(m => m.TeamId == session.TeamId
                && m.IsActive
                && volunteerExaminerIds.Contains(m.VolunteerExaminerId))
            .Select(m => m.VolunteerExaminer)
            .ToListAsync(cancellationToken);

        result.NotOnTeam = volunteerExaminerIds.Distinct().Count() - recipients.Count;
        if (result.NotOnTeam > 0)
        {
            logger.LogWarning(
                "Session invitation for session {SessionId} requested {Requested} recipient(s), {Dropped} of which are not active members of team {TeamId} and were dropped.",
                session.Id, volunteerExaminerIds.Distinct().Count(), result.NotOnTeam, session.TeamId);
        }

        // The team's own From/Reply-To, the same row candidate mail sends from — an invitation that
        // arrived from a different address than every other message the team sends would look like
        // spam to its own volunteers.
        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == session.TeamId, cancellationToken);
        if (emailSettings is null)
        {
            result.Error = "This team has no email From/Reply-To settings yet, so invitations cannot be sent.";
            return result;
        }

        var credentials = session.Team.ToEmailCredentials();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Composed first, sent second (#293). This used to call SendAsync per recipient, and
        // SmtpEmailSender does a full connect + TLS + AUTH + disconnect per message — so a 30-VE
        // roster meant 30 SMTP handshakes inside one POST, with the sender watching a spinner.
        // SendManyAsync holds one connection for the batch.
        var addressable = new List<VolunteerExaminer>(recipients.Count);
        var messages = new List<EmailMessage>(recipients.Count);

        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                // Counted, not silently dropped: "invited 8 of 10" with no explanation is worse than
                // a number the sender can act on by filling in an address.
                result.NoEmailAddress++;
                continue;
            }

            // Text-only exists in the model but nothing can set it while SMS is unbuilt. Honoured
            // anyway, so that when SMS arrives this loop does not need remembering.
            if (recipient.ContactPreference == VeContactPreference.Text)
            {
                result.TextOnlySkipped++;
                continue;
            }

            addressable.Add(recipient);
            messages.Add(new EmailMessage(
                ToAddress: recipient.Email!,
                FromAddress: emailSettings.FromAddress,
                FromDisplayName: emailSettings.FromDisplayName,
                ReplyToAddress: emailSettings.ReplyToAddress,
                Subject: Render(subject, recipient, session, encodeHtml: false),
                HtmlBody: Render(body, recipient, session, encodeHtml: true)));
        }

        // Outcomes come back in the order the messages went in, which is what lets a failure still be
        // attributed to its VE — the per-recipient reporting this fan-out has always done.
        var outcomes = await emailSender.SendManyAsync(credentials, messages, cancellationToken);

        for (var i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i].Sent)
            {
                result.Sent++;
                continue;
            }

            // One bad address must not stop the rest of the invitations going out; that rule now
            // lives in the sender, which is the only layer that can keep the connection open across
            // the failure.
            result.Failed++;
            logger.LogError(outcomes[i].Error, "Failed to send a session invitation to VE {VolunteerExaminerId}", addressable[i].Id);
        }

        dbContext.AddAuditLog(userId, "VeSessionInvitationsSent", nameof(Session), session.Id,
            $"Invitations for session {session.ExamToolsSessionId}: {result.Sent} sent, {result.Failed} failed, " +
            $"{result.NoEmailAddress} with no address, {result.TextOnlySkipped} text-only, " +
            $"{result.NotOnTeam} not on the team.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Session invitations for session {SessionId}: {Result}", session.Id, result);
        return result;
    }

    /// <summary>
    /// Substitutes the <c>{{Token}}</c> placeholders. An unknown token is left exactly as written —
    /// the same choice EmailTemplateRenderer makes, and for the same reason: a visible "{{Typo}}" in
    /// a draft is a bug someone fixes, where a silently empty gap is one nobody notices.
    /// </summary>
    private static string Render(string text, VolunteerExaminer recipient, Session session, bool encodeHtml)
    {
        // HTML-encoded for the body, left alone for the subject — the same rule
        // EmailTemplateRenderer applies, and for the same stated reason: several of these values
        // come from the ExamTools feed, which is fed by public registration intake.
        //
        // This is the case that renderer exists to prevent, and this method skipped it (#260) while
        // its two neighbours (VeSelfServiceLinkService, VeEmailChangeService) both encode — an
        // omission rather than a policy. A Session.Title of
        //   </p><a href="https://evil/">Click to confirm your VE assignment</a>
        // rendered as live markup in every invited VE's mail client. Mail clients strip <script>,
        // so this is link injection for phishing rather than script execution — arriving inside a
        // genuine invitation from the team's real address, which is what makes it convincing.
        string E(string value) => encodeHtml ? WebUtility.HtmlEncode(value) : StripLineBreaks(value);

        return text
            .Replace("{{VeName}}", E(recipient.Name))
            .Replace("{{CallSign}}", E(recipient.CallSign ?? ""))
            .Replace("{{SessionTitle}}", E(session.Title))
            .Replace("{{SessionDate}}", E(session.ScheduledStartUtc.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'")))
            // Lands in href="…" in every real template, so it needs attribute-safe encoding rather
            // than element-safe. HtmlEncode escapes the quote that would break out of the attribute;
            // a URL that survives that is still a URL.
            .Replace("{{ZoomJoinUrl}}", E(session.ZoomJoinUrl ?? ""))
            .Replace("{{TeamName}}", E(session.Team.Name));
    }

    /// <summary>Subject lines are mail headers, and a header ends at a newline. Same rule as
    /// EmailTemplateRenderer's (#261).</summary>
    private static string StripLineBreaks(string value) =>
        value.Contains('\r') || value.Contains('\n')
            ? value.Replace("\r", "").Replace('\n', ' ')
            : value;
}

/// <param name="AlreadyOnRoster">Already assigned to this session in ExamTools — usually a reason not to invite them again.</param>
public record VeInvitationCandidate(
    VolunteerExaminer VolunteerExaminer,
    IReadOnlyList<string> Tags,
    bool AlreadyOnRoster,
    VeEligibility Eligibility)
{
    public bool HasEmail => !string.IsNullOrWhiteSpace(VolunteerExaminer.Email);
}

public class VeInvitationResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }

    /// <summary>Selected but unreachable. Counted so the sender can go and fill in an address rather than wondering.</summary>
    public int NoEmailAddress { get; set; }

    /// <summary>Selected but text-only. Unreachable until SMS exists.</summary>
    public int TextOnlySkipped { get; set; }

    /// <summary>
    /// Requested but not an active member of the session's team, so never contacted (#238). Normally
    /// zero. A non-zero value from an ordinary send means a membership changed while the compose
    /// screen was open; any other cause is a tampered form.
    /// </summary>
    public int NotOnTeam { get; set; }

    public string? Error { get; set; }

    public override string ToString() =>
        $"{Sent} sent, {Failed} failed, {NoEmailAddress} with no address, {TextOnlySkipped} text-only, " +
        $"{NotOnTeam} not on the team";
}
