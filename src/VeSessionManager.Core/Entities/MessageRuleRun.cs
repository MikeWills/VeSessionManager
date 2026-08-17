namespace VeSessionManager.Core.Entities;

/// <summary>
/// One rule firing for one subject (#401) — <b>both</b> the idempotency marker and the run log. See
/// docs/trigger-points.md.
///
/// <para><b>It replaces a column, and does one thing that column could not.</b> Today
/// <c>Candidate.RegistrationConfirmationSentUtc</c> conflates three different outcomes — sent,
/// suppressed because the team was muted, and never applicable — into one nullable timestamp, with no
/// way to tell them apart afterwards. <see cref="Outcome"/> is that distinction.</para>
///
/// <para><b>Keyed by rule, never by trigger.</b> A team can have "remind at 7 days" and "remind at 1
/// day" on one trigger; a per-trigger marker would let either mark the other done. Hence the unique
/// index on <c>(MessageRuleId, SubjectId)</c>.</para>
///
/// <para><b>Only <see cref="MessageRuleOutcome.Sent"/> and <see cref="MessageRuleOutcome.Suppressed"/>
/// stop a subject being scanned again.</b> A <see cref="MessageRuleOutcome.Failed"/> or
/// <see cref="MessageRuleOutcome.NoRecipient"/> row is written too — it is the log, and a failure
/// nobody can see is the thing this table exists to end — but the scanner still returns that subject,
/// and the dispatcher <b>updates this row in place</b> rather than inserting a second one. A failed
/// send has always retried on the next tick, and that has to survive the move onto rules; the unique
/// index is what forces the update rather than allowing a quiet pile of duplicates.</para>
/// </summary>
public class MessageRuleRun
{
    public int Id { get; set; }

    /// <summary>Denormalized from the rule so the log can be scoped to a team without a join, the same way <c>AuditLog.TeamId</c> is.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public int MessageRuleId { get; set; }
    public MessageRule MessageRule { get; set; } = null!;

    /// <summary>
    /// The rule's name as it was when this fired. A snapshot string, not a read through the FK: a
    /// rule renamed later must not silently rewrite the history of what it did, which is the same
    /// reason <see cref="CandidateEmailSend.TemplateLabel"/> is a label.
    /// </summary>
    public required string RuleName { get; set; }

    /// <summary>Snapshotted for the same reason as <see cref="RuleName"/>, and so the log stays readable if the rule is deleted.</summary>
    public MessageTrigger Trigger { get; set; }

    public MessageSubjectType SubjectType { get; set; }

    /// <summary><see cref="Candidate"/>.Id or <see cref="Payment"/>.Id, per <see cref="SubjectType"/>. Not a foreign key: one column cannot point at two tables, and a trigger's subject type is fixed by the trigger.</summary>
    public int SubjectId { get; set; }

    /// <summary>The most recent attempt. Overwritten on a retry, along with <see cref="Outcome"/>.</summary>
    public DateTime FiredUtc { get; set; }

    public MessageRuleOutcome Outcome { get; set; }

    /// <summary>Why, for the outcomes that need one — the failure message, or which recipient was missing. Never the message body: a subject line routinely carries the candidate's own name, and a store holding content is one the PII purge has to keep reaching into.</summary>
    public string? Detail { get; set; }
}
