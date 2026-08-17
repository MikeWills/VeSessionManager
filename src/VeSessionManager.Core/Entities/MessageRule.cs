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
    public required string TemplateKey { get; set; }

    public MessageChannel Channel { get; set; } = MessageChannel.Email;

    public MessageRecipient Recipient { get; set; } = MessageRecipient.Candidate;

    public MessageFanOut FanOut { get; set; } = MessageFanOut.PerRecipient;

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
