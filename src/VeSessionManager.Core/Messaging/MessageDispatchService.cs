using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// The single send path for every rule-driven message (#401) — renders, sends, and writes the
/// <see cref="MessageRuleRun"/> that both records what happened and stops it happening twice.
///
/// <para><b>One path, deliberately.</b> The four hardcoded sends this replaces each had their own
/// copy of "check the mute switch, render, send, stamp a column", and they had already drifted: the
/// FCC-fee reminder settled differently from the other three when a team was muted, and three call
/// sites reported "Sent." for a team that sent nothing (#396). Rendering in particular goes through
/// the existing <c>EmailTemplateRenderer</c> and never a second renderer — a hand-rolled Replace
/// chain is what shipped without HTML-encoding in #260.</para>
///
/// <para><b>Three ways a batch stops before sending, and they are not the same.</b> No EmailSettings
/// row and unconfigured SMTP both leave <i>no marker at all</i>, so the very next tick tries again
/// once an admin finishes setting up — the optional-integration pattern. A muted team writes
/// <see cref="MessageRuleOutcome.Suppressed"/> markers and settles, because a switch turned off on
/// purpose is indefinite and a backlog flushed on re-enable is the failure mode, not the feature.</para>
/// </summary>
public class MessageDispatchService(
    AppDbContext dbContext,
    EmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    TeamIntegrationState integrationState,
    TimeProvider timeProvider,
    ILogger<MessageDispatchService> logger)
{
    public async Task<MessageRuleResult> DispatchAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, IReadOnlyList<MessageSubject> subjects, CancellationToken cancellationToken)
    {
        var result = new MessageRuleResult();
        if (subjects.Count == 0)
        {
            return result;
        }

        if (rule.Channel != MessageChannel.Email)
        {
            // Declared in the model, not yet dispatchable. Refused loudly rather than skipped: only
            // the seeder can create a rule today, so reaching here means something wrote one that
            // nothing can deliver, and a silent skip would look exactly like a quiet week.
            logger.LogError("Rule {RuleId} ({RuleName}) on team {TeamId} asks for channel {Channel}, which is not implemented yet — {Count} subject(s) not delivered",
                rule.Id, rule.Name, team.Id, rule.Channel, subjects.Count);
            return await RecordAllAsync(team, rule, subjects, MessageRuleOutcome.Failed, $"Channel {rule.Channel} is not implemented", result, cancellationToken);
        }

        if (!team.IsEmailConfigured)
        {
            // Skipped quietly with one aggregate line rather than an error per subject, and — the
            // part that matters — with no marker, so everything waiting here goes out on the first
            // tick after credentials are entered. No separate backfill step exists or is needed.
            logger.LogInformation("SMTP is not fully configured for team {TeamId} — {PendingCount} message(s) waiting on rule \"{RuleName}\"; will send automatically once configured",
                team.Id, subjects.Count, rule.Name);
            result.Waiting += subjects.Count;
            return result;
        }

        if (!integrationState.ShouldCall(team, TeamIntegration.Email, $"sending \"{rule.Name}\""))
        {
            return await RecordAllAsync(team, rule, subjects, MessageRuleOutcome.Suppressed, null, result, cancellationToken);
        }

        var credentials = team.ToEmailCredentials();

        foreach (var subject in subjects)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                var toAddress = ResolveAddress(rule, subject, emailSettings);
                if (string.IsNullOrWhiteSpace(toAddress))
                {
                    // Not terminal: an address filled in later should still get the message.
                    await RecordAsync(team, rule, subject, MessageRuleOutcome.NoRecipient, $"No address for recipient {rule.Recipient}", now, cancellationToken);
                    result.NoRecipient++;
                    continue;
                }

                var rendered = await templateRenderer.RenderAsync(team.Id, rule.TemplateKey, subject.Placeholders, cancellationToken);
                if (rendered is null)
                {
                    await RecordAsync(team, rule, subject, MessageRuleOutcome.Failed, $"Template \"{rule.TemplateKey}\" is missing", now, cancellationToken);
                    result.Failed++;
                    continue;
                }

                await emailSender.SendAsync(
                    credentials,
                    new EmailMessage(toAddress, emailSettings.FromAddress, emailSettings.FromDisplayName,
                        emailSettings.ReplyToAddress, rendered.Subject, rendered.Body, rendered.InlineLogo,
                        // The monitoring copy exists to watch what *candidates* receive (#207).
                        // Copying a team's own internal notice back to the same team's monitoring
                        // inbox is noise, so a rule addressed anywhere else carries no Bcc.
                        BccAddress: rule.Recipient == MessageRecipient.Candidate ? emailSettings.BccAddress : null),
                    cancellationToken);

                // Only on a real send. These columns are no longer authoritative, but they are what
                // the candidate Email history screen renders, and stamping one for a message that was
                // suppressed or failed is precisely the lie #396 is about.
                subject.StampLegacySentUtc?.Invoke(now);

                await RecordAsync(team, rule, subject, MessageRuleOutcome.Sent, null, now, cancellationToken);
                result.Sent++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rule \"{RuleName}\" ({RuleId}) failed for {SubjectType} {SubjectId}", rule.Name, rule.Id, subject.SubjectType, subject.SubjectId);
                await RecordAsync(team, rule, subject, MessageRuleOutcome.Failed, Truncate(ex.Message), now, cancellationToken);
                result.Failed++;
            }
        }

        return result;
    }

    private static string? ResolveAddress(MessageRule rule, MessageSubject subject, EmailSettings emailSettings) => rule.Recipient switch
    {
        MessageRecipient.Candidate => subject.CandidateEmail,
        MessageRecipient.TeamAdminAddress => emailSettings.AdminNotificationEmail,
        // SessionLead and DiscordChannel are declared in the model and not resolvable yet. Returning
        // null lands them on NoRecipient with the reason attached, which is retried rather than
        // settled — correct, because what is missing is code, not an address.
        _ => null
    };

    private async Task<MessageRuleResult> RecordAllAsync(
        Team team, MessageRule rule, IReadOnlyList<MessageSubject> subjects, MessageRuleOutcome outcome, string? detail,
        MessageRuleResult result, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var subject in subjects)
        {
            await RecordAsync(team, rule, subject, outcome, detail, now, cancellationToken);
        }

        if (outcome == MessageRuleOutcome.Suppressed)
        {
            result.Suppressed += subjects.Count;
        }
        else
        {
            result.Failed += subjects.Count;
        }

        return result;
    }

    /// <summary>
    /// Writes the marker, and saves immediately — one item at a time, so a crash mid-run never loses
    /// progress already made on others and never re-sends to someone already reached. Every
    /// scan-based job in this app saves this way.
    ///
    /// <para><b>Upsert, not insert.</b> A subject whose last attempt failed comes back around, and the
    /// unique index on <c>(MessageRuleId, SubjectId)</c> means the second attempt has to update the
    /// first row rather than add one. That index is doing real work here: without it a flapping SMTP
    /// server would quietly grow a row per tick per candidate.</para>
    /// </summary>
    private async Task RecordAsync(
        Team team, MessageRule rule, MessageSubject subject, MessageRuleOutcome outcome, string? detail, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var existing = await dbContext.MessageRuleRuns
            .FirstOrDefaultAsync(r => r.MessageRuleId == rule.Id && r.SubjectId == subject.SubjectId, cancellationToken);

        if (existing is null)
        {
            dbContext.MessageRuleRuns.Add(new MessageRuleRun
            {
                TeamId = team.Id,
                MessageRuleId = rule.Id,
                RuleName = rule.Name,
                Trigger = rule.Trigger,
                SubjectType = subject.SubjectType,
                SubjectId = subject.SubjectId,
                FiredUtc = nowUtc,
                Outcome = outcome,
                Detail = detail
            });
        }
        else
        {
            existing.RuleName = rule.Name;
            existing.FiredUtc = nowUtc;
            existing.Outcome = outcome;
            existing.Detail = detail;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>An exception message goes in a log column, not a body — SMTP errors can carry the whole rejected envelope.</summary>
    private static string Truncate(string message) => message.Length <= 400 ? message : message[..400];
}
