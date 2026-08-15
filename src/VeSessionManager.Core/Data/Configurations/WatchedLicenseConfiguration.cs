using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="WatchedLicense"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class WatchedLicenseConfiguration : IEntityTypeConfiguration<WatchedLicense>
{
    public void Configure(EntityTypeBuilder<WatchedLicense> b)
    {
        b.HasOne(w => w.Team).WithMany().HasForeignKey(w => w.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(w => w.AddedByUser).WithMany().HasForeignKey(w => w.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        // Uniqueness is per team, not global: two teams may each independently watch the same
        // call sign, and neither should be able to see or clobber the other's row.
        b.HasIndex(w => new { w.TeamId, w.CallSign }).IsUnique();
        // The refresh job's query is "least recently checked first", across all teams.
        b.HasIndex(w => w.LastCheckedUtc);
        
    }
}
