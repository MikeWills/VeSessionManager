using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeTagAssignment"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeTagAssignmentConfiguration : IEntityTypeConfiguration<VeTagAssignment>
{
    public void Configure(EntityTypeBuilder<VeTagAssignment> b)
    {
        b.HasKey(a => new { a.VeTeamMembershipId, a.VeTagId });
        b.HasOne(a => a.VeTeamMembership).WithMany(m => m.TagAssignments).HasForeignKey(a => a.VeTeamMembershipId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.VeTag).WithMany(t => t.Assignments).HasForeignKey(a => a.VeTagId).OnDelete(DeleteBehavior.Cascade);
        
    }
}
