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
        int teamId, MessageTrigger trigger, string name, string subject, string body, int? parameterHours,
        MessageRecipient recipient, int userId, CancellationToken cancellationToken,
        MessageChannel channel = MessageChannel.Email, ulong? discordChannelId = null, MessageFanOut fanOut = MessageFanOut.PerRecipient,
        MessageEnvelope? envelope = null)
    {
        envelope ??= MessageEnvelope.Default;
        var validation = await ValidateAsync(teamId, trigger, name, subject, body, parameterHours, recipient, channel, discordChannelId, fanOut, envelope, cancellationToken);
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
            Subject = subject.Trim(),
            Body = body,
            Channel = channel,
            DiscordChannelId = channel == MessageChannel.Discord ? discordChannelId : null,
            Recipient = recipient,
            FanOut = fanOut,
            ReplyToSource = envelope.ReplyToSource,
            ReplyToOverride = envelope.ReplyToOverride,
            CcAddress = envelope.CcAddress,
            BccAddress = envelope.BccAddress,
            MonitoringCopyOncePerRun = envelope.MonitoringCopyOncePerRun,
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
            $"Team {teamId} rule '{rule.Name}' on {trigger} ({Describe(parameterHours)}) via {DescribeDestination(rule)}.", now);
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
        int messageRuleId, string name, string subject, string body, int? parameterHours,
        MessageRecipient recipient, int userId, CancellationToken cancellationToken,
        MessageChannel channel = MessageChannel.Email, ulong? discordChannelId = null, MessageFanOut fanOut = MessageFanOut.PerRecipient,
        MessageEnvelope? envelope = null)
    {
        envelope ??= MessageEnvelope.Default;
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        var validation = await ValidateAsync(rule.TeamId, rule.Trigger, name, subject, body, parameterHours, recipient, channel, discordChannelId, fanOut, envelope, cancellationToken);
        if (validation != MessageRuleActionResult.Success)
        {
            return validation;
        }

        rule.Name = name.Trim();
        rule.Subject = subject.Trim();
        rule.Body = body;
        rule.ParameterHours = parameterHours;
        rule.Recipient = recipient;
        rule.Channel = channel;
        rule.DiscordChannelId = channel == MessageChannel.Discord ? discordChannelId : null;
        rule.FanOut = fanOut;
        rule.ReplyToSource = envelope.ReplyToSource;
        rule.ReplyToOverride = envelope.ReplyToOverride;
        rule.CcAddress = envelope.CcAddress;
        rule.BccAddress = envelope.BccAddress;
        rule.MonitoringCopyOncePerRun = envelope.MonitoringCopyOncePerRun;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "MessageRuleUpdated", nameof(MessageRule), rule.Id,
            $"Team {rule.TeamId} rule '{rule.Name}' on {rule.Trigger} set to {Describe(parameterHours)} via {DescribeDestination(rule)}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// Copies a rule — how you get "and again a week earlier" without retyping the template,
    /// recipient, channel and envelope.
    ///
    /// <para><b>The copy starts switched off.</b> A duplicate is made in order to change something,
    /// and a rule that starts sending the instant it exists gives nobody the chance. Switching it on
    /// is one click and a decision.</para>
    ///
    /// <para><b>It gets its own <c>CreatedUtc</c>, and that is the safety property.</b> A copy of a
    /// rule created a year ago would otherwise inherit a year-old bound and reach everybody the
    /// original had already passed — the exact "3000 emails because you added a rule" failure, arriving
    /// through the back door. It also starts with no markers, which is correct: it has sent nothing.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> DuplicateAsync(int messageRuleId, int userId, CancellationToken cancellationToken)
    {
        var original = await dbContext.MessageRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (original is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var copy = new MessageRule
        {
            TeamId = original.TeamId,
            Name = $"{original.Name} (copy)",
            Trigger = original.Trigger,
            ParameterHours = original.ParameterHours,
            // The words come with it. Copying is how somebody makes a second pre-session message and
            // changes the timing — the reuse the old template split was meant to provide, in the one
            // direction that actually holds (same trigger, different schedule).
            Subject = original.Subject,
            Body = original.Body,
            Channel = original.Channel,
            DiscordChannelId = original.DiscordChannelId,
            Recipient = original.Recipient,
            FanOut = original.FanOut,
            ReplyToSource = original.ReplyToSource,
            ReplyToOverride = original.ReplyToOverride,
            CcAddress = original.CcAddress,
            BccAddress = original.BccAddress,
            MonitoringCopyOncePerRun = original.MonitoringCopyOncePerRun,
            IsEnabled = false,
            CreatedUtc = now
        };
        dbContext.MessageRules.Add(copy);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(userId, "MessageRuleCreated", nameof(MessageRule), copy.Id,
            $"Team {copy.TeamId} rule '{copy.Name}' copied from '{original.Name}' ({original.Id}), switched off.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MessageRuleActionResult.Success;
    }

    /// <summary>
    /// Just the timing and the recipient — what the Email Templates editor offers inline, so somebody
    /// writing the wording can change when it goes without leaving the page.
    ///
    /// <para><b>Narrower than <see cref="UpdateAsync"/> on purpose.</b> That one takes every field, so
    /// calling it from a form that only knows about two would quietly reset the rule's channel,
    /// Discord id and envelope to their defaults. A partial form needs a partial method; the
    /// alternative is a caller that has to remember to round-trip five fields it never shows.</para>
    ///
    /// <para><b>The trigger is deliberately not settable here, or anywhere.</b> A rule's
    /// <see cref="MessageRuleRun"/> markers are keyed to it, and their <c>SubjectId</c> means a
    /// candidate under one trigger and a payment under another — moving a rule between triggers would
    /// reinterpret every marker it has already written, and the visible consequence is a re-send.
    /// Wanting a different moment means wanting a different rule.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> UpdateScheduleAsync(
        int messageRuleId, int? parameterHours, MessageRecipient recipient, int userId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        var definition = MessageTriggerDefinitions.For(rule.Trigger);
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

        // Only meaningful for an email rule; a channel post addresses nobody, so the form does not
        // offer it and this leaves the stored value alone.
        if (rule.Channel == MessageChannel.Email)
        {
            if (!definition.LegalRecipients.Contains(recipient))
            {
                return MessageRuleActionResult.RecipientNotLegal;
            }

            rule.Recipient = recipient;
        }

        rule.ParameterHours = parameterHours;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "MessageRuleUpdated", nameof(MessageRule), rule.Id,
            $"Team {rule.TeamId} rule '{rule.Name}' on {rule.Trigger} rescheduled to {Describe(parameterHours)} via {DescribeDestination(rule)}.", now);
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
    /// <para>Disabling settles nothing: no markers are written. Re-enabling used to pick up whoever
    /// was eligible <i>at that moment</i>, bounded only by <c>CreatedUtc</c> — which meant somebody
    /// who became eligible while it was off <i>was</i> chased retroactively, despite this doc comment
    /// once claiming otherwise. Fixed 2026-08-25 (Mike: "if a message is off then I turn on, it's not
    /// supposed to send backlog either") by stamping <see cref="MessageRule.EnabledSinceUtc"/> on an
    /// actual off-to-on transition — every scanner reads it through
    /// <see cref="Messaging.MessageRuleEligibility.FloorUtc"/> now, not <c>CreatedUtc</c> alone.</para>
    /// </summary>
    public async Task<MessageRuleActionResult> SetEnabledAsync(int messageRuleId, bool enabled, int userId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.MessageRules.FirstOrDefaultAsync(r => r.Id == messageRuleId, cancellationToken);
        if (rule is null)
        {
            return MessageRuleActionResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Only an actual off-to-on transition moves the floor. Stamping it on every call — including
        // a redundant "enable" on an already-enabled rule — would push the floor forward each time
        // and start silently hiding candidates who'd been legitimately waiting since the real
        // enable moment.
        if (enabled && !rule.IsEnabled)
        {
            rule.EnabledSinceUtc = now;
        }
        rule.IsEnabled = enabled;

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
        int teamId, MessageTrigger trigger, string name, string subject, string body, int? parameterHours,
        MessageRecipient recipient, MessageChannel channel, ulong? discordChannelId, MessageFanOut fanOut,
        MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MessageRuleActionResult.NameRequired;
        }

        // MessageTriggerDefinitions.For throws for anything not in All, which now includes
        // MessageTrigger.SentByHand (#417) as well as a value someone posted by hand. Refused as
        // validation rather than escaping as a 500.
        if (!MessageTriggerDefinitions.All.Any(d => d.Trigger == trigger))
        {
            return MessageRuleActionResult.TriggerNotConfigurable;
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

        if (channel == MessageChannel.Discord)
        {
            // A Discord rule with no channel posts nowhere, forever, and looks configured while doing
            // it — the same class of silent nothing every other check here refuses.
            if (discordChannelId is not { } channelId || channelId == 0)
            {
                return MessageRuleActionResult.DiscordChannelRequired;
            }
        }
        else
        {
            // A digest is one message covering everybody, which only makes sense where nobody is
            // individually addressed. On email it would mean one message to one address listing every
            // other candidate — a disclosure, not a feature.
            if (fanOut == MessageFanOut.SingleDigest)
            {
                return MessageRuleActionResult.DigestNeedsAChannel;
            }

            if (!definition.LegalRecipients.Contains(recipient))
            {
                return MessageRuleActionResult.RecipientNotLegal;
            }
        }

        // A message owns its own words now (2026-08-21), so there is no key to look up and no way to
        // point at something that does not exist — the whole TemplateNotFound class of failure is
        // gone rather than guarded. What is left is that there have to BE words: an empty body sends
        // a blank email, which is worse than refusing.
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            return MessageRuleActionResult.MessageRequired;
        }

        // The audience check went with it. A message is authored against its own trigger, so it can
        // only ever use that trigger's placeholders — the mismatch #409 guarded against (a VE-worded
        // template rendered through a candidate-subject scanner, every token blank, and the send
        // *succeeding*) is now impossible to express rather than caught.

        return ValidateEnvelope(channel, envelope);
    }

    /// <summary>
    /// The two envelope constraints, each of which exists because the obvious configuration is a
    /// mistake rather than a preference. A third — refusing Cc on candidate-facing mail — was removed
    /// 2026-08-25; see the comment below.
    /// </summary>
    private static MessageRuleActionResult ValidateEnvelope(MessageChannel channel, MessageEnvelope envelope)
    {
        // A channel post has no envelope at all — nobody is addressed, so there is nothing to reply to
        // and nobody to copy. Refused rather than ignored, so a rule cannot carry settings that look
        // like they do something.
        if (channel == MessageChannel.Discord)
        {
            return envelope.ReplyToSource != MessageReplyToSource.EmailSettings
                   || envelope.CcAddress is not null || envelope.BccAddress is not null
                ? MessageRuleActionResult.EnvelopeNeedsEmail
                : MessageRuleActionResult.Success;
        }

        if (envelope.ReplyToSource == MessageReplyToSource.Custom && string.IsNullOrWhiteSpace(envelope.ReplyToOverride))
        {
            return MessageRuleActionResult.ReplyToRequired;
        }

        // Cc on a candidate-facing rule used to be refused outright here — a Cc'd person cannot
        // unsubscribe (the footer's link belongs to the To recipient) and every candidate sees the
        // address. Mike, 2026-08-25: "I don't care, I want to CC my email in this case so they can
        // see that I know." A team that wants the visibility — the point, not a side effect — is now
        // free to accept the tradeoff. See MessageRule.CcAddress.

        return MessageRuleActionResult.Success;
    }

    private static string Describe(int? parameterHours) =>
        parameterHours is { } hours ? $"{hours}h" : "no delay";

    /// <summary>Where the message actually goes, for the audit line — "the candidate" says nothing useful about a rule that posts to a chat room.</summary>
    private static string DescribeDestination(MessageRule rule) => rule.Channel == MessageChannel.Discord
        ? $"Discord channel {rule.DiscordChannelId}{(rule.FanOut == MessageFanOut.SingleDigest ? " (one digest)" : " (one post each)")}"
        : $"email to {rule.Recipient}";
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

    /// <summary>
    /// No trigger point is registered for that value — either <c>SentByHand</c>, which is a marker on a
    /// run rather than something a rule can use, or a value somebody posted by hand.
    /// </summary>
    TriggerNotConfigurable,

    /// <summary>
    /// The message has no subject or no body. An empty one sends a blank email, which is worse than
    /// refusing to save it.
    ///
    /// <para>Replaced <c>TemplateNotFound</c> and <c>TemplateAudienceMismatch</c> on 2026-08-21, when
    /// a message started owning its own words. Both of those guarded consequences of the split: a rule
    /// pointing at a key that did not exist, and a VE-worded template rendered through a
    /// candidate-subject scanner with every token blank. Neither can be expressed now.</para>
    /// </summary>
    MessageRequired,

    /// <summary>A Discord rule arrived with no channel id. It would post nowhere, forever, while looking configured (#401 PR4).</summary>
    DiscordChannelRequired,

    /// <summary>
    /// A digest was asked for on email. One message covering everybody only makes sense where nobody
    /// is individually addressed — on email it means one message to one address listing every other
    /// candidate, which is a disclosure rather than a feature.
    /// </summary>
    DigestNeedsAChannel,

    /// <summary>Reply-To, Cc or Bcc set on a Discord rule. Nobody is addressed on a channel post, so there is nothing to reply to and nobody to copy (#401 PR4).</summary>
    EnvelopeNeedsEmail,

    /// <summary>Reply-To set to a custom address, with no address.</summary>
    ReplyToRequired
}
