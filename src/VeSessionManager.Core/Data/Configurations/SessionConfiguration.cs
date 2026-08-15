using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="Session"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.HasIndex(s => s.ExamToolsSessionId).IsUnique();
        b.HasOne(s => s.Vec).WithMany(v => v.Sessions).HasForeignKey(s => s.VecId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.Team).WithMany(t => t.Sessions).HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.FeeConfiguration).WithMany(f => f.Sessions).HasForeignKey(s => s.FeeConfigurationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.TestingCompletedByUser).WithMany().HasForeignKey(s => s.TestingCompletedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.VecSubmittedByUser).WithMany().HasForeignKey(s => s.VecSubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
        // NOTE: RetainedAmountOverrideByUser is the one User FK here still left to EF's
        // convention (ClientSetNull), which contradicts this block's opening statement that every
        // FK is Restrict. Audit T21 asked for it to be pinned, and it was — then reverted, on
        // purpose:
        //
        //   * SQLite implements an FK change as a full table rebuild, which EF reports as "cannot
        //     be executed in a transaction". An interrupted deploy would leave the database
        //     partially migrated and needing manual repair.
        //   * It guards against a user being deleted — and there is no delete path in this app at
        //     all. UserManagementService only deactivates; see #188, which is still open.
        //
        // So the risk is real today and the benefit is not. #188 has to decide FK behaviour across
        // thirteen Restrict relationships anyway; this one belongs in that migration, alone, where
        // the rebuild can be planned rather than ridden along with an index change.
        // Money, so two decimal places rather than the provider's default. Matches
        // FeeConfiguration's amounts above.
        b.Property(s => s.RetainedAmountOverride).HasPrecision(10, 2);
        // The session list's default ordering and its date-range filter, per team — the busiest
        // query in the app.
        b.HasIndex(s => new { s.TeamId, s.ScheduledStartUtc });
        
    }
}
