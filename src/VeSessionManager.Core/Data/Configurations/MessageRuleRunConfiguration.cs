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
        b.HasIndex(r => new { r.MessageRuleId, r.SubjectId }).IsUnique();

        // "What did this team's rules do lately", which is the run log's only other read.
        b.HasIndex(r => new { r.TeamId, r.FiredUtc });

        b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);

        // Restrict, deliberately, and against the instinct that a child row follows its parent: this
        // row is the record that a message was sent to a real person. A rule deleted next year must
        // not take the evidence of what it did with it — which is also why RuleName and Trigger are
        // snapshotted onto the row rather than read through this FK.
        b.HasOne(r => r.MessageRule).WithMany(r => r.Runs).HasForeignKey(r => r.MessageRuleId).OnDelete(DeleteBehavior.Restrict);
    }
}
