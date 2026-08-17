using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Creating and editing a team's <see cref="MessageRule"/>s (#401, PR2) — the write side of the
/// Message Rules admin screen.
///
/// <para><b>Delete and switch-off are both here, and they answer different questions.</b> Switching
/// off is "not right now" — the rule keeps its name, its hours and its place on the screen, and
/// switching it back on resumes from that moment. Deleting is "we do not do this", and it is a real
/// delete: the row goes.</para>
///
/// <para>Two things had to change before delete could be honest, and both are worth knowing.
/// <c>EmailDefaultsSeeder</c> used to re-add any trigger a team had no rule for, so a deleted rule
/// came back on the next Worker start — it now seeds once per team and records that it has
/// (<see cref="Team.MessageRulesSeededUtc"/>). And <see cref="MessageRuleRun.MessageRuleId"/> became
/// nullable, so the record of what a rule sent to real people survives the rule itself, which is what
/// <c>RuleName</c> and <c>Trigger</c> were snapshotted onto the row for in the first place.</para>
///
/// <para><b>Nothing here changes a rule's trigger.</b> The <see cref="MessageRuleRun"/> markers that
/// stop a rule firing twice are keyed by rule, and their <c>SubjectId</c> means a candidate for one
/// trigger and a payment for another — so moving a rule between triggers would reinterpret every
/// marker it already has. Creating a second rule is the supported answer, and it costs nothing.</para>
///
/// <para>Authorization is the caller's job, against the rule's <b>own</b> <c>TeamId</c> rather than a
/// posted one — see how the page does it, and #238 for what the other way costs.</para>
/// </summary>
public class MessageRuleAdminService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>
    /// A year. Not a real limit so much as a guard against a typo that reads as a working rule: 2400
    /// entered where 240 was meant is a hundred days, which for a pre-session reminder simply never
    /// fires and looks like the feature being broken.
    /// </summary>
    public const int MaxParameterHours = 8760;

    public async Task<MessageRuleActionResult> CreateAsync(
        int teamId, MessageTrigger trigger, string name, string templateKey, int? parameterHours,
        MessageRecipient recipient, int userId, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(teamId, trigger, name, templateKey, parameterHours, recipient, cancellationToken);
        if (validation != MessageRuleActionResult.Success)
        {
            return validation;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rule = new MessageRule
        {
            TeamId = teamId,
            Name = name.Trim(),
            Trigger = trigger,
            ParameterHours = parameterHours,
            TemplateKey = templateKey,
            Channel = MessageChannel.Email,
            Recipient = recipient,
            FanOut = MessageFanOut.PerRecipient,
            IsEnabled = true,
            // "Now", and it is the whole safety property rather than a timestamp: every scan is
            // bounded by it, so a rule created this morning cannot reach anybody whose moment passed
            // yesterday. See MessageRule.CreatedUtc — this is the line that stops "add a 7-day
            // reminder" from meaning "email everyone already 8 days in".
            CreatedUtc = now
        };
        dbContext.MessageRules.Add(rule);

        // Saved before the audit row is written, so the row records the rule's real id rather than
        // the 0 an unsaved entity has. Two round trips, and worth it: an audit entry pointing at
        // entity 0 cannot be traced back to anything.
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(userId, "MessageRuleCreated", nameof(MessageRule), rule.Id,
            $"Team {teamId} rule '{rule.Name}' on {trigger} ({Describe(parameterHours)}) to {recipient}, template '{templateKey}'.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// Everything about a rule except its trigger and its <c>CreatedUtc</c>.
    ///
    /// <para><b>Changing the hours does not re-open anyone already settled</b>, and that is worth
    /// knowing before doing it: the markers are per rule, so widening a 24-hour reminder to 48 reaches
    /// people not yet reminded, not people already reminded at 24. Narrowing it reaches nobody new.
    /// <c>CreatedUtc</c> stays put for the same reason it exists — refreshing it on every edit would
    /// mean a typo corrected an hour later silently skips everybody in between.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> UpdateAsync(
        int messageRuleId, string name, string templateKey, int? parameterHours,
        MessageRecipient recipient, int userId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        var validation = await ValidateAsync(rule.TeamId, rule.Trigger, name, templateKey, parameterHours, recipient, cancellationToken);
        if (validation != MessageRuleActionResult.Success)
        {
            return validation;
        }

        rule.Name = name.Trim();
        rule.TemplateKey = templateKey;
        rule.ParameterHours = parameterHours;
        rule.Recipient = recipient;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "MessageRuleUpdated", nameof(MessageRule), rule.Id,
            $"Team {rule.TeamId} rule '{rule.Name}' on {rule.Trigger} set to {Describe(parameterHours)} to {recipient}, template '{templateKey}'.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// Deletes a rule outright. For "we do not do this" — <see cref="SetEnabledAsync"/> is the answer
    /// to "not right now".
    ///
    /// <para><b>The runs it already produced stay.</b> They are the record that real people were
    /// emailed, and they carry the rule's name and trigger as snapshots precisely so they can outlive
    /// it; the FK is <c>SetNull</c>. An orphaned run no longer guards anything, which is correct — so
    /// re-creating the same rule later starts clean, and its own <c>CreatedUtc</c> is what stops that
    /// reaching anybody whose moment has already passed.</para>
    ///
    /// <para>Safe from resurrection only because <c>EmailDefaultsSeeder</c> seeds once per team and
    /// records it. Deleting the last rule on a trigger is a legitimate answer, and the next Worker
    /// start must not overrule it.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> DeleteAsync(int messageRuleId, int userId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        // Detached by hand rather than left to the database's ON DELETE, because EF would otherwise
        // try to enforce the old required FK on any run it happens to be tracking. Loaded and nulled
        // explicitly so the behaviour is the same whichever provider is underneath — EF InMemory has
        // no referential actions at all, and a delete that only works on SQLite is one the tests
        // cannot see.
        var runs = await dbContext.MessageRuleRuns.Where(r => r.MessageRuleId == rule.Id).ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            run.MessageRuleId = null;
        }

        dbContext.MessageRules.Remove(rule);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "MessageRuleDeleted", nameof(MessageRule), rule.Id,
            $"Team {rule.TeamId} rule '{rule.Name}' on {rule.Trigger} ({Describe(rule.ParameterHours)}) deleted. {runs.Count} run(s) kept.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// Switches a rule on or off, keeping it on the screen — "not right now", where
    /// <see cref="DeleteAsync"/> is "we do not do this".
    ///
    /// <para>Disabling settles nothing: no markers are written, so re-enabling picks up whoever is
    /// eligible <i>at that moment</i>, bounded as always by <c>CreatedUtc</c>. Somebody whose moment
    /// passed while it was off is not chased retroactively, which is the same promise the rule made
    /// when it was created.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> SetEnabledAsync(int messageRuleId, bool enabled, int userId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        rule.IsEnabled = enabled;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, enabled ? "MessageRuleEnabled" : "MessageRuleDisabled", nameof(MessageRule), rule.Id,
            $"Team {rule.TeamId} rule '{rule.Name}' on {rule.Trigger} {(enabled ? "enabled" : "disabled")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// The rules a create/edit form must satisfy. Every one of these describes a rule that would look
    /// configured and do nothing, or the wrong thing — which is the failure mode worth refusing, since
    /// a rule that silently never fires is indistinguishable from a quiet week.
    /// </summary>
    private async Task<MessageRuleActionResult> ValidateAsync(
        int teamId, MessageTrigger trigger, string name, string templateKey, int? parameterHours,
        MessageRecipient recipient, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MessageRuleActionResult.NameRequired;
        }

        var definition = MessageTriggerDefinitions.For(trigger);

        // A time-relative trigger with no parameter has not been answered — the parameter *is* the
        // question that trigger asks.
        if (definition.Mechanism == MessageTriggerMechanism.TimeRelative)
        {
            if (parameterHours is not { } hours)
            {
                return MessageRuleActionResult.ParameterRequired;
            }

            if (hours < 1 || hours > MaxParameterHours)
            {
                return MessageRuleActionResult.ParameterOutOfRange;
            }
        }

        if (!definition.LegalRecipients.Contains(recipient))
        {
            return MessageRuleActionResult.RecipientNotLegal;
        }

        // Checked against this team's own templates, and it has to be: a rule pointing at a key that
        // does not exist records Failed on every tick, forever, and the only sign is a log line.
        if (string.IsNullOrWhiteSpace(templateKey)
            || !await dbContext.EmailTemplates.AnyAsync(t => t.TeamId == teamId && t.Key == templateKey, cancellationToken))
        {
            return MessageRuleActionResult.TemplateNotFound;
        }

        return MessageRuleActionResult.Success;
    }

    private static string Describe(int? parameterHours) =>
        parameterHours is { } hours ? $"{hours}h" : "no delay";
}

public enum MessageRuleActionResult
{
    Success,
    NotFound,

    /// <summary>The name is what the run log records and the only thing telling two rules on one trigger apart, so a blank one is not a rule.</summary>
    NameRequired,

    /// <summary>A time-relative trigger arrived with no hours. Refused rather than defaulted: a rule that fires at a time nobody chose is worse than one that will not save.</summary>
    ParameterRequired,

    /// <summary>Zero, negative, or more than a year. See <see cref="MessageRuleAdminService.MaxParameterHours"/>.</summary>
    ParameterOutOfRange,

    /// <summary>
    /// This trigger cannot address that recipient — a registration confirmation sent to the team's own
    /// admin inbox is a mistake, not a configuration. The legal set is on
    /// <c>MessageTriggerDefinitions</c>, which is also what the form offers.
    /// </summary>
    RecipientNotLegal,

    /// <summary>No template with that key on this team. A rule pointing at nothing records Failed on every tick with only a log line to show for it.</summary>
    TemplateNotFound
}
