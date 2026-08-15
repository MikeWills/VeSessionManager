using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeVecAccreditation"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeVecAccreditationConfiguration : IEntityTypeConfiguration<VeVecAccreditation>
{
    public void Configure(EntityTypeBuilder<VeVecAccreditation> b)
    {
        b.HasIndex(a => new { a.VolunteerExaminerId, a.VecId }).IsUnique();
        b.HasOne(a => a.VolunteerExaminer).WithMany(v => v.VecAccreditations).HasForeignKey(a => a.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.Vec).WithMany().HasForeignKey(a => a.VecId).OnDelete(DeleteBehavior.Restrict);
        
    }
}
