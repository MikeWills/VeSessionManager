namespace VeSessionManager.Core.Entities;

/// <summary>
/// One team's decision that a particular message goes out at a particular trigger point (#401) — the
/// row that replaces a hardcoded send in a service. See docs/trigger-points.md.
///
/// <para><b>Per team, and only per team.</b> Not per VEC and not global: a VEC is a shared reference
/// table here (see docs/multi-team.md), and the thing being configured is what this team's candidates
/// receive over this team's own SMTP.</para>
///
/// <para><b>Zero, one or many rules per trigger.</b> "Remind at 7 days" and "remind at 1 day" are two
/// rules on one trigger, which is why <see cref="MessageRuleRun"/> markers are keyed by rule rather
/// than by trigger.</para>
/// </summary>
public class MessageRule
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>
    /// Free text, and what the run log records rather than a generated description. A team that has
    /// three reminders on one trigger needs to be able to tell them apart in the log, and only the
    /// person who created them can say which is which.
    /// </summary>
    public required string Name { get; set; }

    public MessageTrigger Trigger { get; set; }

    /// <summary>
    /// The trigger's parameter, in <b>hours</b> — never days, and never a calendar date.
    ///
    /// <para>This is #220 made structural. The day-before reminder used to compare against "tomorrow"
    /// as a UTC calendar date; sessions run in the evening Eastern, which is already tomorrow in raw
    /// UTC, so the "day before" reminder went out on the day of the session. Hours between two
    /// instants has no calendar date in it, so there is no timezone to get wrong.</para>
    ///
    /// <para>Null for a state trigger, which has no parameter. See
    /// <c>MessageTriggerDefinitions</c> for each trigger's default.</para>
    /// </summary>
    public int? ParameterHours { get; set; }

    /// <summary>
    /// Which of this team's <see cref="EmailTemplate.Key"/>s to render.
    ///
    /// <para>A string rather than a foreign key, for the same reason
    /// <see cref="CandidateEmailSend.TemplateLabel"/> is one: a template renamed or removed must not
    /// take history with it, and a team writes its own templates (#144), so the set is not fixed by
    /// what the code looks up.</para>
    /// </summary>
    /// <summary>
    /// The subject line, and <see cref="Body"/> the words. <b>The message owns them</b> (2026-08-21).
    ///
    /// <para>These used to live on an <c>EmailTemplate</c> the rule pointed at by key. That split is
    /// what made the tag list unanswerable: available placeholders depend on the trigger, the body was
    /// authored somewhere that had no trigger, so the editor could show nothing. Mike: <i>"there's no
    /// way currently that you can link up a template to the correct rule so that a person can have the
    /// right tags available to them."</i></para>
    ///
    /// <para>The reuse that split bought — one body, several schedules — is better served by copying
    /// a message and changing the timing, which is what somebody wanting a second pre-session note
    /// actually does. Reuse across <i>triggers</i> was the part that could not work, because the tags
    /// differ.</para>
    /// </summary>
    public required string Subject { get; set; }

    /// <inheritdoc cref="Subject"/>
    public required string Body { get; set; }

    public MessageChannel Channel { get; set; } = MessageChannel.Email;

    /// <summary>
    /// Which Discord channel a <see cref="MessageChannel.Discord"/> rule posts into (#401 PR4). Null
    /// for an email rule.
    ///
    /// <para><b>Per rule rather than per team</b>, so one team can put its session reminders in
    /// #announcements and its new-licensee congratulations in #general. The guild is still the team's
    /// (<see cref="Team.DiscordGuildId"/>) — the bot is only in one per team.</para>
    /// </summary>
    public ulong? DiscordChannelId { get; set; }

    public MessageRecipient Recipient { get; set; } = MessageRecipient.Candidate;

    public MessageFanOut FanOut { get; set; } = MessageFanOut.PerRecipient;

    /// <summary>
    /// Where a reply goes (#401 PR4). Defaults to <see cref="MessageReplyToSource.EmailSettings"/>,
    /// which is what every message did before this field existed.
    ///
    /// <para><b>This is the field teams actually want, and <c>From</c> is not.</b> Changing the From
    /// address means SPF, DKIM and DMARC on a domain this app does not control — get it wrong and mail
    /// silently goes to spam, which is the worst possible failure for a reminder. Reply-To has no such
    /// constraint: it changes who hears the answer, which is the real request behind "can it come from
    /// the session lead".</para>
    /// </summary>
    public MessageReplyToSource ReplyToSource { get; set; } = MessageReplyToSource.EmailSettings;

    /// <summary>Used only when <see cref="ReplyToSource"/> is <see cref="MessageReplyToSource.Custom"/>.</summary>
    public string? ReplyToOverride { get; set; }

    /// <summary>
    /// A visible copy on every message this rule sends (#401 PR4). Null for almost every rule, and
    /// that is the right default.
    ///
    /// <para><b>Cc discloses.</b> Everyone on a fan-out sees this address, and the person at it sees
    /// every recipient's name in the To line if a client shows it. Worse, they cannot unsubscribe —
    /// the footer's link belongs to the To recipient — so a Cc on candidate-facing mail is a
    /// standing copy nobody can stop. Deliberately not offered on the admin form for a
    /// candidate-facing rule.</para>
    /// </summary>
    public string? CcAddress { get; set; }

    /// <summary>
    /// A silent copy, over and above the team-wide monitoring Bcc in <c>EmailSettings</c>.
    ///
    /// <para>See <see cref="MonitoringCopyOncePerRun"/> for the multiplication problem this shares
    /// with <see cref="CcAddress"/>.</para>
    /// </summary>
    public string? BccAddress { get; set; }

    /// <summary>
    /// Whether this rule's own <see cref="CcAddress"/>/<see cref="BccAddress"/> go on <b>one</b>
    /// message per run rather than on every one (#401 PR4). Default true, and the default is the
    /// point.
    ///
    /// <para>Forty candidates on a fan-out means forty copies of the same message into the same
    /// inbox, which stops being monitoring and becomes a reason to filter the folder — at which point
    /// nobody is watching at all. One copy answers "what did this rule actually send today".</para>
    ///
    /// <para><b>The team-wide <c>EmailSettings.BccAddress</c> is untouched by this</b> and still goes
    /// on every candidate-facing message, as it has since #207. That is existing behaviour outside
    /// this field's remit; changing it is a separate decision about what monitoring is for.</para>
    /// </summary>
    public bool MonitoringCopyOncePerRun { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// <b>Load-bearing, not bookkeeping.</b> Every trigger scan is bounded by this: a subject whose
    /// trigger moment fell before the rule existed is never returned. That is what makes "adding a
    /// rule never fires it for anyone already past the moment" true by construction rather than by
    /// somebody remembering — the failure mode being designed against is a new rule at 7 days
    /// mailing every candidate who is already 8 days in.
    ///
    /// <para>How it bounds depends on the mechanism: a state trigger compares the stored moment
    /// directly, a time-relative trigger compares <c>anchor ± ParameterHours</c>. See the scanners.</para>
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    public ICollection<MessageRuleRun> Runs { get; set; } = [];
}
