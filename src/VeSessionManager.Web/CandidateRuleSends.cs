using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <param name="Label">The rule's own name, snapshotted on the run — "Reminder 24 hours before the session" says which rule, where "Reminder email" could not.</param>
public record RuleSend(string Label, DateTime SentUtc, MessageTrigger Trigger);

/// <summary>
/// What a candidate has actually been sent by the rule engine (#415), read from
/// <c>MessageRuleRun</c> rather than from the legacy <c>Candidate.*SentUtc</c> columns.
///
/// <para><b>Why the columns could not stay.</b> They are a fixed set of app-defined names, which is
/// the opposite of what #401 is for. Three separate symptoms of the same cause: a rule on
/// <c>CandidateTested</c> or <c>LicenseGranted</c> has no column at all, so its mail was invisible;
/// two rules on one trigger share one column, so "remind at 7 days" and "remind at 1 day" collapsed
/// into a single line with whichever timestamp landed last; and the FCC fee reminder stamps
/// <c>Candidate.FccFeeReminderSentUtc</c>, which the history never read, so it has never appeared at
/// all.</para>
///
/// <para><b>Batched deliberately.</b> The session Detail page builds one row per candidate, so a
/// per-candidate query here would be an N+1 across a full roster.</para>
/// </summary>
public static class CandidateRuleSends
{
    private static readonly IReadOnlyList<RuleSend> None = [];

    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<RuleSend>>> LoadAsync(
        AppDbContext dbContext, IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
    {
        if (candidateIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<RuleSend>>();
        }

        var rows = await dbContext.MessageRuleRuns
            .AsNoTracking()
            .Where(r => r.SubjectType == MessageSubjectType.Candidate
                        && candidateIds.Contains(r.SubjectId)
                        // Only what was actually delivered. A Suppressed or Failed row is real
                        // history, but this list answers "what has this person received" and
                        // listing either as received would be a lie of exactly the kind #396 was.
                        && r.Outcome == MessageRuleOutcome.Sent
                        // Addressed to the candidate, over email. CandidateTested and LicenseGranted
                        // may both address the team's own inbox instead — a message *about* this
                        // candidate that they never saw — and a Discord rule reaches a room, not a
                        // person. A run whose rule has since been deleted is kept: MessageRuleId is
                        // nullable precisely so history outlives the rule, and dropping those would
                        // undo that on the page where it matters most.
                        && (r.MessageRule == null
                            || (r.MessageRule.Recipient == MessageRecipient.Candidate
                                && r.MessageRule.Channel == MessageChannel.Email)))
            .Select(r => new { r.SubjectId, r.RuleName, r.FiredUtc, r.Trigger })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.SubjectId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RuleSend>)[.. g
                    .OrderBy(r => r.FiredUtc)
                    .Select(r => new RuleSend(r.RuleName, r.FiredUtc, r.Trigger))]);
    }

    /// <summary>Nothing recorded for this candidate — a separate method so call sites do not each spell out the empty case.</summary>
    public static IReadOnlyList<RuleSend> For(
        IReadOnlyDictionary<int, IReadOnlyList<RuleSend>> byCandidate, int candidateId) =>
        byCandidate.TryGetValue(candidateId, out var sends) ? sends : None;
}
