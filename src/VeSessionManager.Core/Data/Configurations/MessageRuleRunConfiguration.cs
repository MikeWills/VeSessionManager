using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="MessageRuleRun"/> (#401).
/// </summary>
public class MessageRuleRunConfiguration : IEntityTypeConfiguration<MessageRuleRun>
{
    public void Configure(EntityTypeBuilder<MessageRuleRun> b)
    {
        // The idempotency guarantee itself, and the reason a retry has to update rather than insert.
        // Unique in the database rather than only in the dispatcher's logic: two Worker ticks
        // overlapping is not a hypothetical, and the failure this table exists to prevent is a
        // duplicate email.
        //
        // MessageRuleId is nullable since #401 PR2 (a deleted rule leaves its runs behind), and SQLite
        // treats NULLs as distinct in a unique index — so any number of orphaned rows coexist happily.
        // That is the behaviour wanted rather than one tolerated: they describe rules that no longer
        // exist, so they have nothing left to make unique.
        b.HasIndex(r => new { r.MessageRuleId, r.SubjectId }).IsUnique();

        // "What did this team's rules do lately", which is the run log's only other read.
        b.HasIndex(r => new { r.TeamId, r.FiredUtc });

        b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);

        // SetNull, deliberately, and against the instinct that a child row follows its parent: this
        // row is the record that a message was sent to a real person. A rule deleted next year must
        // not take the evidence of what it did with it — which is also why RuleName and Trigger are
        // snapshotted onto the row rather than read through this FK.
        //
        // Restrict would be the other way to protect the log, and it is what this was until delete
        // existed (#401 PR2) — but it protects it by refusing the delete, which makes the log a reason
        // an admin cannot tidy up their own rules. SetNull keeps both.
        b.HasOne(r => r.MessageRule).WithMany(r => r.Runs).HasForeignKey(r => r.MessageRuleId).OnDelete(DeleteBehavior.SetNull);
    }
}
