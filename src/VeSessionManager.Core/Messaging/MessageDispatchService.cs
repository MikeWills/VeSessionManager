using VeSessionManager.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
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
/// purpose is indefinite and a backlog flushed on re-enable is the failure mode, not the feature.
/// <see cref="SuppressByFccIssueAsync"/> (2026-08-26) is the same shape again, one level up: a global
/// "FCC has a known issue" switch rather than a per-team one, checked before the channel split since
/// it applies regardless of Email/Discord.</para>
/// </summary>
public class MessageDispatchService(
    AppDbContext dbContext,
    EmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    IDiscordChannelMessageClient discordClient,
    TeamIntegrationState integrationState,
    SystemSettingsService systemSettingsService,
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

        subjects = await SuppressByFccIssueAsync(team, rule, subjects, result, cancellationToken);
        if (subjects.Count == 0)
        {
            return result;
        }

        if (rule.Channel == MessageChannel.Discord)
        {
            return await DispatchToDiscordAsync(team, rule, subjects, result, cancellationToken);
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

        // One email per session rather than one per candidate (#491) — the VE-facing counterpart to
        // PostDigestAsync's Discord PerSession branch below. Its own method because "who does a
        // batched message go To" has no Discord equivalent (a channel post addresses nobody), so the
        // recipient resolution, Cc/Bcc-once-per-run bookkeeping and per-group send loop are all new
        // rather than shared with the per-subject loop just below.
        if (rule.FanOut == MessageFanOut.PerSession)
        {
            return await DispatchEmailPerSessionAsync(team, rule, emailSettings, credentials, subjects, result, cancellationToken);
        }

        // The rule's own Cc/Bcc, resolved once. Forty candidates on a fan-out would otherwise be
        // forty copies of the same message into the same inbox, which stops being monitoring and
        // becomes a folder somebody filters — see MessageRule.MonitoringCopyOncePerRun.
        var monitoringCopyRemaining = rule.MonitoringCopyOncePerRun ? 1 : int.MaxValue;
        var replyToCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var subject in subjects)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                // A list rather than one address: a role recipient is inherently several people, and
                // that is the whole point of the trigger x recipient work. Candidate and
                // TeamAdminAddress still resolve to exactly one, so their behaviour is unchanged.
                var toAddresses = await MessageRecipientResolver.ResolveAsync(
                    dbContext, team, rule.Recipient, subject.SessionLeadCallSign, subject.CandidateEmail,
                    emailSettings.AdminNotificationEmail, cancellationToken);

                if (toAddresses.Count == 0)
                {
                    // Not terminal: an address filled in later should still get the message. That is
                    // truer than ever now — a role with no users today may have one next week.
                    await RecordAsync(team, rule, subject, MessageRuleOutcome.NoRecipient, $"No address for recipient {rule.Recipient}", now, cancellationToken);
                    result.NoRecipient++;
                    continue;
                }

                var rendered = await templateRenderer.RenderTextAsync(team.Id, rule.Subject, rule.Body, subject.Placeholders, rule.Name, cancellationToken);

                // The team-wide monitoring copy exists to watch what *candidates* receive (#207).
                // Copying a team's own internal notice back to the same team's monitoring inbox is
                // noise, so a rule addressed anywhere else carries no Bcc. Unchanged by PR4: this one
                // still goes on every message, which is existing behaviour and a separate decision
                // from the rule's own copies below.
                var teamMonitoringBcc = rule.Recipient == MessageRecipient.Candidate ? emailSettings.BccAddress : null;

                var takeMonitoringCopy = monitoringCopyRemaining > 0;
                var ruleCc = takeMonitoringCopy ? NullIfBlank(rule.CcAddress) : null;
                var ruleBcc = takeMonitoringCopy ? NullIfBlank(rule.BccAddress) : null;
                if (takeMonitoringCopy && (ruleCc is not null || ruleBcc is not null))
                {
                    monitoringCopyRemaining--;
                }

                var replyTo = await ResolveReplyToAsync(rule, subject, emailSettings, replyToCache, cancellationToken);
                var icsAttachment = BuildCalendarInvite(rule, subject);

                // One message each rather than one message with several To addresses: staff should not
                // see each other's addresses on a header, and a per-address send means one bad address
                // cannot take the rest down with it.
                foreach (var toAddress in toAddresses)
                {
                    await emailSender.SendAsync(
                        credentials,
                        new EmailMessage(toAddress, emailSettings.FromAddress, emailSettings.FromDisplayName,
                            replyTo,
                            rendered.Subject, rendered.Body, rendered.InlineLogo,
                            // Two Bccs can be in play; the rule's own is folded in beside the team's.
                            BccAddress: teamMonitoringBcc ?? ruleBcc,
                            CcAddress: ruleCc,
                            IcsAttachment: icsAttachment),
                        cancellationToken);
                }

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

    /// <summary>
    /// A global "FCC has a known issue" switch (2026-08-26), checked before anything else in
    /// <see cref="DispatchAsync"/> because it applies regardless of channel. Only subjects carrying a
    /// non-null <see cref="MessageSubject.FccPopulation"/> — today, only <see cref="MessageTrigger.FccFeeOutstanding"/> —
    /// are ever affected; every other trigger's subjects pass through untouched, most runs skipping
    /// the settings lookup entirely.
    ///
    /// <para>Suppressed the same way a muted team's integration is: a terminal
    /// <see cref="MessageRuleOutcome.Suppressed"/> row, not a silent skip. A silent skip would let the
    /// candidate re-enter <c>ScanAsync</c>'s eligible set the moment the switch flips off, and every
    /// suppressed candidate would fire in the same batch — the exact backlog-on-re-enable failure
    /// <see cref="MessageRuleEligibility"/> already exists to prevent for a different kind of "off."
    /// Marking them Suppressed means the switch flipping back off changes nothing for them; only a
    /// candidate whose own reminder becomes newly due afterward ever gets one.</para>
    /// </summary>
    private async Task<IReadOnlyList<MessageSubject>> SuppressByFccIssueAsync(
        Team team, MessageRule rule, IReadOnlyList<MessageSubject> subjects, MessageRuleResult result, CancellationToken cancellationToken)
    {
        if (!subjects.Any(s => s.FccPopulation is not null))
        {
            return subjects;
        }

        var settings = await systemSettingsService.GetAsync(cancellationToken);
        if (!settings.FccIssueActive)
        {
            return subjects;
        }

        var suppressed = new List<MessageSubject>();
        var proceeding = new List<MessageSubject>();
        foreach (var subject in subjects)
        {
            var suppress = subject.FccPopulation switch
            {
                FccCandidatePopulation.NewLicense => settings.FccIssueSuppressNewLicenseReminders,
                FccCandidatePopulation.Upgrade => settings.FccIssueSuppressUpgradeReminders,
                _ => false
            };

            (suppress ? suppressed : proceeding).Add(subject);
        }

        if (suppressed.Count > 0)
        {
            await RecordAllAsync(team, rule, suppressed, MessageRuleOutcome.Suppressed,
                "Suppressed: a known FCC-wide processing issue is flagged for this candidate population", result, cancellationToken);
        }

        return proceeding;
    }

    /// <summary>
    /// Posting a rule's message into a Discord channel (#401 PR4).
    ///
    /// <para><b>Nothing per-person can reach here, structurally.</b> This path builds no
    /// <see cref="EmailMessage"/>, so there is no From, no Reply-To, no monitoring Bcc and no
    /// unsubscribe footer to accidentally carry into a room full of people — those are properties of
    /// writing to one person, and a channel post is not that. The plan for this PR named the risk;
    /// the answer is that the code has no field to put them in rather than a check that remembers
    /// not to.</para>
    ///
    /// <para><b>Markers are still per subject even for a digest.</b> One post covering twelve
    /// candidates writes twelve rows, so the next tick knows all twelve are done — a single marker
    /// keyed to the post would leave eleven of them looking unsent, and the thirteenth candidate to
    /// arrive would re-announce the first twelve.</para>
    /// </summary>
    private async Task<MessageRuleResult> DispatchToDiscordAsync(
        Team team, MessageRule rule, IReadOnlyList<MessageSubject> subjects, MessageRuleResult result, CancellationToken cancellationToken)
    {
        // Same shape as the SMTP check above, and the same reason for the difference between the two:
        // unconfigured leaves no marker and retries, muted settles.
        if (!team.IsDiscordConfigured || !discordClient.IsConfigured || rule.DiscordChannelId is not { } channelId)
        {
            logger.LogInformation("Discord is not fully configured for team {TeamId} — {PendingCount} message(s) waiting on rule \"{RuleName}\"; will post automatically once it is",
                team.Id, subjects.Count, rule.Name);
            result.Waiting += subjects.Count;
            return result;
        }

        if (!integrationState.ShouldCall(team, TeamIntegration.Discord, $"posting \"{rule.Name}\""))
        {
            return await RecordAllAsync(team, rule, subjects, MessageRuleOutcome.Suppressed, null, result, cancellationToken);
        }

        var guildId = team.DiscordGuildId!.Value;

        if (rule.FanOut == MessageFanOut.SingleDigest)
        {
            return await PostDigestAsync(team, rule, guildId, channelId, subjects, result, cancellationToken);
        }

        if (rule.FanOut == MessageFanOut.PerSession)
        {
            // Grouped rather than batched. Subjects with no session share the null group and render
            // without the session tokens, instead of being dropped — a payment-subject rule set to
            // PerSession should still say something.
            foreach (var group in subjects.GroupBy(s => s.Session?.SessionId))
            {
                result = await PostDigestAsync(team, rule, guildId, channelId, [.. group],
                    result, cancellationToken, group.First().Session);
            }

            return result;
        }

        foreach (var subject in subjects)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                var rendered = await templateRenderer.RenderTextAsync(team.Id, rule.Subject, rule.Body, subject.Placeholders, rule.Name, cancellationToken);

                await discordClient.PostMessageAsync(guildId, channelId, DiscordMessageText.FromHtml(rendered.Body),
                    DiscordMentionPolicy.ParseRoleIds(team.DiscordMentionableRoleIds), cancellationToken);

                // No legacy ...SentUtc stamp: those columns mean "this candidate was emailed", and a
                // channel post is not an email to them.
                await RecordAsync(team, rule, subject, MessageRuleOutcome.Sent, null, now, cancellationToken);
                result.Sent++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rule \"{RuleName}\" ({RuleId}) failed to post for {SubjectType} {SubjectId}", rule.Name, rule.Id, subject.SubjectType, subject.SubjectId);
                await RecordAsync(team, rule, subject, MessageRuleOutcome.Failed, Truncate(ex.Message), now, cancellationToken);
                result.Failed++;
            }
        }

        return result;
    }

    /// <summary>
    /// One post covering every subject in the batch — what <see cref="MessageFanOut.SingleDigest"/>
    /// selects, and the reason that field exists at all: a forty-candidate session on
    /// <c>PerRecipient</c> is forty posts in a row.
    ///
    /// <para>The template is rendered once, against the digest's own placeholders rather than any one
    /// candidate's — <c>{{Count}}</c> and <c>{{Subjects}}</c>. A per-candidate token like
    /// <c>{{CandidateFirstName}}</c> has no answer here and renders blank, which is why the admin
    /// screen offers a different placeholder list for a digest rule.</para>
    ///
    /// <para><b>All or nothing.</b> One post, so one failure means none of the subjects is marked —
    /// they all come back on the next tick, and the post is retried whole. Recording some as sent
    /// would be recording that a post said something it never said.</para>
    /// </summary>
    private async Task<MessageRuleResult> PostDigestAsync(
        Team team, MessageRule rule, ulong guildId, ulong channelId, IReadOnlyList<MessageSubject> subjects,
        MessageRuleResult result, CancellationToken cancellationToken, MessageSessionContext? session = null)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var placeholders = new Dictionary<string, string>
        {
            ["Count"] = subjects.Count.ToString(),
            ["Subjects"] = string.Join("\n", subjects.Select(s => "• " + s.DigestLabel))
        };

        // Only PerSession has one session to speak about, so only PerSession gets these. A batch
        // spanning several sessions cannot answer them and therefore does not offer them.
        if (session is not null)
        {
            placeholders["SessionTitle"] = session.Title;

            // ForCandidate despite this not going to a candidate: it is the formatter that renders
            // Eastern, and #116 asks for "xx:xx eastern time". Never EasternTimeFormatter — that
            // lives in the Web project and is unreachable from Core, which is how candidate email
            // spent months rendering UTC (#205).
            placeholders["SessionDate"] = SessionTimeFormatter.ForCandidate(session.ScheduledStartUtc);

            // Candidates registered on the session, deliberately distinct from {{Count}} above, which
            // is how many this rule is firing for. Subjects are filtered by having an email, not
            // being purged, and not already having a terminal run, so the two differ constantly —
            // and "x candidates registered to test" means this one.
            placeholders["RegisteredCount"] = session.RegisteredCandidateCount.ToString();
        }

        try
        {
            var rendered = await templateRenderer.RenderTextAsync(team.Id, rule.Subject, rule.Body, placeholders, rule.Name, cancellationToken);

            await discordClient.PostMessageAsync(guildId, channelId, DiscordMessageText.FromHtml(rendered.Body),
                    DiscordMentionPolicy.ParseRoleIds(team.DiscordMentionableRoleIds), cancellationToken);

            foreach (var subject in subjects)
            {
                await RecordAsync(team, rule, subject, MessageRuleOutcome.Sent, "Included in a digest post", now, cancellationToken);
            }

            result.Sent += subjects.Count;
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rule \"{RuleName}\" ({RuleId}) failed to post its digest of {Count} subject(s)", rule.Name, rule.Id, subjects.Count);
            return await RecordAllAsync(team, rule, subjects, MessageRuleOutcome.Failed, Truncate(ex.Message), result, cancellationToken);
        }
    }

    /// <summary>
    /// Groups subjects by session and sends one email per group (#491) — the null-session group
    /// (a subject the scanner never loaded one for) still gets a send rather than being dropped, same
    /// tolerance as <see cref="PostDigestAsync"/>'s Discord PerSession branch, even though
    /// <c>ValidateAsync</c> means it's rare in practice: every trigger offering PerSession on email
    /// today is candidate-subject and populates Session.
    /// </summary>
    private async Task<MessageRuleResult> DispatchEmailPerSessionAsync(
        Team team, MessageRule rule, EmailSettings emailSettings, EmailCredentials credentials,
        IReadOnlyList<MessageSubject> subjects, MessageRuleResult result, CancellationToken cancellationToken)
    {
        var monitoringCopyRemaining = rule.MonitoringCopyOncePerRun ? 1 : int.MaxValue;
        var replyToCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in subjects.GroupBy(s => s.Session?.SessionId))
        {
            var groupSubjects = (IReadOnlyList<MessageSubject>)[.. group];
            var representative = groupSubjects[0];
            var now = timeProvider.GetUtcNow().UtcDateTime;

            try
            {
                var toAddresses = await MessageRecipientResolver.ResolveAsync(
                    dbContext, team, rule.Recipient, representative.SessionLeadCallSign, representative.CandidateEmail,
                    emailSettings.AdminNotificationEmail, cancellationToken);

                if (toAddresses.Count == 0)
                {
                    // Same reasoning as the per-subject loop: not terminal, since an address filled in
                    // later (a VE record gaining an email, a role gaining a user) should still get the
                    // next tick's summary.
                    foreach (var subject in groupSubjects)
                    {
                        await RecordAsync(team, rule, subject, MessageRuleOutcome.NoRecipient, $"No address for recipient {rule.Recipient}", now, cancellationToken);
                    }

                    result.NoRecipient += groupSubjects.Count;
                    continue;
                }

                // Same placeholder set PostDigestAsync builds for Discord's PerSession posts, so a
                // team already using {{RegisteredCount}}/{{SessionTitle}}/{{SessionDate}} on a Discord
                // rule can reuse the same body text on an email rule.
                var placeholders = new Dictionary<string, string>
                {
                    ["Count"] = groupSubjects.Count.ToString(),
                    ["Subjects"] = string.Join("\n", groupSubjects.Select(s => "• " + s.DigestLabel))
                };

                if (representative.Session is { } session)
                {
                    placeholders["SessionTitle"] = session.Title;
                    placeholders["SessionDate"] = SessionTimeFormatter.ForCandidate(session.ScheduledStartUtc);
                    placeholders["RegisteredCount"] = session.RegisteredCandidateCount.ToString();
                }

                var rendered = await templateRenderer.RenderTextAsync(team.Id, rule.Subject, rule.Body, placeholders, rule.Name, cancellationToken);

                var takeMonitoringCopy = monitoringCopyRemaining > 0;
                var ruleCc = takeMonitoringCopy ? NullIfBlank(rule.CcAddress) : null;
                var ruleBcc = takeMonitoringCopy ? NullIfBlank(rule.BccAddress) : null;
                if (takeMonitoringCopy && (ruleCc is not null || ruleBcc is not null))
                {
                    monitoringCopyRemaining--;
                }

                var replyTo = await ResolveReplyToAsync(rule, representative, emailSettings, replyToCache, cancellationToken);
                var icsAttachment = BuildCalendarInvite(rule, representative);

                foreach (var toAddress in toAddresses)
                {
                    await emailSender.SendAsync(
                        credentials,
                        new EmailMessage(toAddress, emailSettings.FromAddress, emailSettings.FromDisplayName,
                            replyTo, rendered.Subject, rendered.Body, rendered.InlineLogo,
                            BccAddress: ruleBcc, CcAddress: ruleCc, IcsAttachment: icsAttachment),
                        cancellationToken);
                }

                // No legacy ...SentUtc stamp, for the same reason DispatchToDiscordAsync's channel
                // post skips it: those columns mean "this candidate was personally emailed", and a
                // session summary goes to the VE, not to any candidate in it.
                foreach (var subject in groupSubjects)
                {
                    await RecordAsync(team, rule, subject, MessageRuleOutcome.Sent, "Included in a per-session summary", now, cancellationToken);
                }

                result.Sent += groupSubjects.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rule \"{RuleName}\" ({RuleId}) failed to send its per-session summary for session {SessionId}",
                    rule.Name, rule.Id, representative.Session?.SessionId);
                result = await RecordAllAsync(team, rule, groupSubjects, MessageRuleOutcome.Failed, Truncate(ex.Message), result, cancellationToken);
            }
        }

        return result;
    }

    /// <summary>
    /// Where a reply goes (#401 PR4). Falls back to the team's own address whenever the rule's choice
    /// cannot be honoured — a reply reaching the team is worse than one reaching the session lead, and
    /// a reply reaching nobody is worse than both.
    ///
    /// <para><b>The call sign is checked for usability, not merely for presence.</b> ExamTools puts a
    /// literal <c>&lt;UNKNOWN&gt;</c> in this field, which once fused two people into one VE record —
    /// <c>CallSign.Normalize</c> is what refuses a placeholder rather than looking one up.</para>
    ///
    /// <para>Cached per run because a session's whole cohort shares one lead: forty candidates would
    /// otherwise be forty identical lookups.</para>
    /// </summary>
    private async Task<string> ResolveReplyToAsync(
        MessageRule rule, MessageSubject subject, EmailSettings emailSettings,
        Dictionary<string, string?> cache, CancellationToken cancellationToken)
    {
        if (rule.ReplyToSource == MessageReplyToSource.Custom)
        {
            return NullIfBlank(rule.ReplyToOverride) ?? emailSettings.ReplyToAddress;
        }

        if (rule.ReplyToSource != MessageReplyToSource.SessionLead)
        {
            return emailSettings.ReplyToAddress;
        }

        if (CallSign.Normalize(subject.SessionLeadCallSign) is not { } callSign)
        {
            return emailSettings.ReplyToAddress;
        }

        if (!cache.TryGetValue(callSign, out var leadEmail))
        {
            leadEmail = await dbContext.VolunteerExaminers
                .Where(v => v.CallSign == callSign)
                .Select(v => v.Email)
                .FirstOrDefaultAsync(cancellationToken);
            cache[callSign] = leadEmail;

            if (string.IsNullOrWhiteSpace(leadEmail))
            {
                logger.LogInformation(
                    "Rule \"{RuleName}\" asks for the session lead's address, but {CallSign} has no VE record with an email — replies go to the team instead",
                    rule.Name, callSign);
            }
        }

        return NullIfBlank(leadEmail) ?? emailSettings.ReplyToAddress;
    }

    /// <summary>
    /// Built here rather than in a scanner (#491): a scanner's job is deciding who's due, and every
    /// subject on one session's run would otherwise build the identical bytes over again.
    ///
    /// <para><b>Defensive against a rule that shouldn't exist, not just the happy path.</b>
    /// <c>MessageRuleAdminService.ValidateAsync</c> already refuses saving
    /// <c>IncludeCalendarInvite</c> on a Discord rule or a trigger whose scanner never sets
    /// <c>MessageSubject.Session</c> — but validation only guards the save path, and a row can reach
    /// this method however it got here (a direct DB edit, a future migration). Null here just means no
    /// attachment, never a throw.</para>
    ///
    /// <para><b>The UID is keyed on the session, not the send.</b> The same session's registration
    /// confirmation and day-before reminder reuse it, so a calendar client updates one event instead of
    /// creating two — see <see cref="IcsInviteBuilder.Build"/>'s own remarks on why that matters.</para>
    /// </summary>
    private static EmailAttachment? BuildCalendarInvite(MessageRule rule, MessageSubject subject)
    {
        if (!rule.IncludeCalendarInvite || subject.Session is not { } session)
        {
            return null;
        }

        var ics = IcsInviteBuilder.Build(
            $"session-{session.SessionId}@ve-ops", session.Title, session.ScheduledStartUtc,
            session.DurationMinutes, session.ZoomJoinUrl);

        return new EmailAttachment("invite.ics", "text/calendar; method=PUBLISH; charset=utf-8",
            System.Text.Encoding.UTF8.GetBytes(ics));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
