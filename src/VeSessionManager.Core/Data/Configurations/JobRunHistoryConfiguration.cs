using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="JobRunHistory"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class JobRunHistoryConfiguration : IEntityTypeConfiguration<JobRunHistory>
{
    public void Configure(EntityTypeBuilder<JobRunHistory> b)
    {
        b.HasOne(j => j.Team).WithMany().HasForeignKey(j => j.TeamId).OnDelete(DeleteBehavior.Restrict);
        // How the ops dashboard reads this table: one job's recent runs, for one team, newest
        // first. The table only grows, so an unindexed scan gets slower every day it works.
        b.HasIndex(j => new { j.TeamId, j.JobName, j.StartedUtc });
        
    }
}
