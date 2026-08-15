using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="ReconciliationFinding"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class ReconciliationFindingConfiguration : IEntityTypeConfiguration<ReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<ReconciliationFinding> b)
    {
        // One standing row per (team, kind, remote session) — the sweep refreshes rather than
        // re-adds, so a discrepancy that persists for a month is one finding, not thirty.
        // Unique because a duplicate would double-count the badge, which is the one number here
        // anybody reads at a glance.
        b.HasIndex(f => new { f.TeamId, f.Kind, f.ExamToolsSessionId }).IsUnique();

        // The findings page and the badge both filter on "still open".
        b.HasIndex(f => new { f.TeamId, f.ResolvedUtc });

        b.HasOne(f => f.Team).WithMany().HasForeignKey(f => f.TeamId).OnDelete(DeleteBehavior.Cascade);
        
    }
}
