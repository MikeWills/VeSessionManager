using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Writing to a team's VEs from the directory (#191) — not about one session, unlike
/// <see cref="VeSessionInvitationService"/>, which exists to get the Zoom link in front of people for
/// a particular sitting.
///
/// <para><b>One team sends, always.</b> A VE can belong to several teams, but a message goes out over
/// one team's SMTP with that team's From and Reply-To — so the team is chosen on the screen and the
/// recipients are that team's active members. Sending "as" a team somebody does not belong to would
/// be a stranger's address in their inbox, which is the shape phishing takes.</para>
///
/// <para>Rendering goes through <see cref="EmailTemplateRenderer.RenderTextAsync"/> rather than a
/// private substitution: that is the lesson of #260, where this service's sibling hand-rolled one and
/// shipped it without HTML-encoding.</para>
/// </summary>
public class VeMessageService(
    AppDbContext dbContext,
    EmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    TeamIntegrationState integrationState,
    VeUnsubscribeService unsubscribeService,
    TimeProvider timeProvider,
    ILogger<VeMessageService> logger)
{
    /// <summary>
    /// Guarantees the unsubscribe link is in the message (#191). If the draft places
    /// <c>{{UnsubscribeUrl}}</c> itself, it is left exactly where the author put it; otherwise a
    /// footer carrying it is appended.
    ///
    /// <para><b>Appended rather than required</b>, because the alternative is a rule somebody has to
    /// remember on every send, and the one time it is forgotten is a message with no way out of it.
    /// CAN-SPAM asks for a clear and conspicuous mechanism, not a particular position, so the safe
    /// default costs nothing.</para>
    /// </summary>
    internal static string WithUnsubscribeFooter(string body) =>
        body.Contains("{{" + VolunteerExaminerPlaceholderValues.UnsubscribeUrl + "}}", StringComparison.Ordinal)
            ? body
            : body +
              "\n<hr style=\"margin-top:24px;border:none;border-top:1px solid #ddd;\" />" +
              "\n<p style=\"font-size:12px;color:#666;\">You are receiving this because you are a volunteer examiner with {{TeamName}}. " +
              "<a href=\"{{UnsubscribeUrl}}\">Unsubscribe from these emails</a>.</p>";

    /// <summary>Everyone who could be written to: the team's active members, annotated with what would stop a message reaching them.</summary>
    public async Task<IReadOnlyList<VeMessageRecipient>> GetRecipientsAsync(int teamId, CancellationToken cancellationToken)
    {
        var memberships = await dbContext.VeTeamMemberships
            .Include(m => m.VolunteerExaminer)
            .Include(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .Where(m => m.TeamId == teamId && m.IsActive)
            // Two sibling collections off each membership would multiply against each other in one
            // statement across the whole roster — the same reason the invitation screen splits (#298).
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return [.. memberships
            .Select(m => new VeMessageRecipient(
                m.VolunteerExaminer,
                [.. m.TagAssignments.Select(a => a.VeTag.Name).OrderBy(n => n)]))
            .OrderBy(r => r.VolunteerExaminer.Name)];
    }

    public async Task<VeMessageResult> SendAsync(
        int teamId, IReadOnlyList<int> volunteerExaminerIds, string subject, string body, string templateLabel,
        int userId, CancellationToken cancellationToken)
    {
        var result = new VeMessageResult();

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            result.Error = "That team no longer exists.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            result.Error = "A message needs both a subject and a body.";
            return result;
        }

        if (!team.IsEmailConfigured)
        {
            result.Error = "This team has no SMTP settings, so nothing can be sent. Set them in Team Settings.";
            return result;
        }

        var emailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == teamId, cancellationToken);
        if (emailSettings is null)
        {
            result.Error = "This team has no email From/Reply-To settings yet, so nothing can be sent.";
            return result;
        }

        // An error rather than a quiet "sent 0" — somebody is standing at a button waiting to hear
        // what happened, which is the case the settle-without-doing rule is wrong for.
        if (!integrationState.ShouldCall(team, TeamIntegration.Email, "sending a message to VEs"))
        {
            result.Error = "Email is switched off for this team, so nothing was sent. Turn it back on in Team Settings.";
            return result;
        }

        // Scoped to active members of the sending team (#238). The ids arrive from a posted form, so
        // "the screen only offered this team's VEs" is a default, not a constraint — unscoped, this
        // sends attacker-authored text from the team's own SMTP to any VolunteerExaminer row on the
        // deployment, including other teams' rosters and retired members. That is exactly the bug the
        // invitation screen shipped with.
        var recipients = await dbContext.VeTeamMemberships
            .Where(m => m.TeamId == teamId && m.IsActive && volunteerExaminerIds.Contains(m.VolunteerExaminerId))
            .Select(m => m.VolunteerExaminer)
            .ToListAsync(cancellationToken);

        result.NotOnTeam = volunteerExaminerIds.Distinct().Count() - recipients.Count;
        if (result.NotOnTeam > 0)
        {
            logger.LogWarning(
                "VE message for team {TeamId} requested {Requested} recipient(s), {Dropped} of which are not active members and were dropped.",
                teamId, volunteerExaminerIds.Distinct().Count(), result.NotOnTeam);
        }

        // Composed first, sent second: one SMTP handshake for the batch rather than one per VE (#293).
        var addressable = new List<VolunteerExaminer>(recipients.Count);
        var messages = new List<EmailMessage>(recipients.Count);

        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                result.NoEmailAddress++;
                continue;
            }

            // The whole point of an unsubscribe, checked at the send rather than only in the picker:
            // the ids come from a form, and somebody who asked to stop must stop even if the screen
            // was open before they clicked it.
            if (recipient.EmailUnsubscribedUtc is not null)
            {
                result.Unsubscribed++;
                continue;
            }

            // Present in the model but unreachable until SMS exists. Honoured here so that when it
            // does, this loop does not need remembering.
            if (recipient.ContactPreference == VeContactPreference.Text)
            {
                result.TextOnlySkipped++;
                continue;
            }

            var unsubscribeUrl = unsubscribeService.BuildUrl(recipient);
            var rendered = await templateRenderer.RenderTextAsync(
                teamId, subject, WithUnsubscribeFooter(body),
                VolunteerExaminerPlaceholderValues.For(recipient, team.Name, unsubscribeUrl), templateLabel, cancellationToken);

            addressable.Add(recipient);
            messages.Add(new EmailMessage(
                recipient.Email!, emailSettings.FromAddress, emailSettings.FromDisplayName,
                emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo));
            // No BccAddress: the monitoring copy is for candidate-facing mail (#207). A VE is a
            // member of the team that would be watching, not somebody it is corresponding with.
        }

        var outcomes = await emailSender.SendManyAsync(team.ToEmailCredentials(), messages, cancellationToken);

        for (var i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i].Sent)
            {
                result.Sent++;
                continue;
            }

            result.Failed++;
            logger.LogError(outcomes[i].Error, "Failed to send a message to VE {VolunteerExaminerId}", addressable[i].Id);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // One row for the batch, matching VeSessionInvitationsSent. Deliberately no per-VE send log
        // like CandidateEmailSend: that one exists so a second pass over a session can skip whoever
        // already had the email, and there is no equivalent pass here — a message to the roster is a
        // one-off, not a worklist. TeamId is left null because the entry is attributed to the person
        // who sent it, and a VolunteerExaminer belongs to no single team (#86).
        dbContext.AddAuditLog(userId, "VeMessageSent", nameof(Team), teamId,
            $"\"{templateLabel}\" to VEs on team {team.Name}: {result}", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("VE message for team {TeamId}: {Result}", teamId, result);
        return result;
    }
}

/// <summary>A VE who could be written to, with what the screen needs to say about them. Contact details are deliberately not exposed — only whether one exists.</summary>
public record VeMessageRecipient(VolunteerExaminer VolunteerExaminer, IReadOnlyList<string> Tags)
{
    public bool HasEmail => !string.IsNullOrWhiteSpace(VolunteerExaminer.Email);

    /// <summary>Set to text-only, so unreachable until SMS exists. Shown rather than silently disabled, since it is a preference somebody chose rather than missing data.</summary>
    public bool IsTextOnly => VolunteerExaminer.ContactPreference == VeContactPreference.Text;

    /// <summary>Asked to stop receiving email (#191). Distinct from IsTextOnly so the screen can say which it is — one is a preference, the other is a request.</summary>
    public bool IsUnsubscribed => VolunteerExaminer.EmailUnsubscribedUtc is not null;

    public bool CanReceive => HasEmail && !IsTextOnly && !IsUnsubscribed;
}

/// <summary>Same shape as VeInvitationResult and CandidateEmailBatchResult — a partial outcome is the normal case for a fan-out over addresses people typed.</summary>
public class VeMessageResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }

    /// <summary>Chosen but unreachable. Counted so somebody can go and fill in an address rather than wondering which two never arrived.</summary>
    public int NoEmailAddress { get; set; }

    /// <summary>Chosen but set to text only. Unreachable until SMS exists.</summary>
    public int TextOnlySkipped { get; set; }

    /// <summary>Chosen but has asked to stop receiving email (#191). Reported rather than hidden, so the team knows to telephone them about a session.</summary>
    public int Unsubscribed { get; set; }

    /// <summary>Requested but not an active member of the sending team, so never contacted (#238). Normally zero; anything else is a roster change mid-compose, or a tampered form.</summary>
    public int NotOnTeam { get; set; }

    /// <summary>Set when nothing was attempted at all — no SMTP, no email settings, a blank draft, or email switched off.</summary>
    public string? Error { get; set; }

    public override string ToString() =>
        $"{Sent} sent, {Failed} failed, {NoEmailAddress} with no address, {TextOnlySkipped} text-only, {Unsubscribed} unsubscribed, {NotOnTeam} not on the team";
}
