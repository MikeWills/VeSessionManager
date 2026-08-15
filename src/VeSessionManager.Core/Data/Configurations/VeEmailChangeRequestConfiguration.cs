using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeEmailChangeRequest"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeEmailChangeRequestConfiguration : IEntityTypeConfiguration<VeEmailChangeRequest>
{
    public void Configure(EntityTypeBuilder<VeEmailChangeRequest> b)
    {
        b.HasIndex(r => r.TokenHash).IsUnique();
        b.HasOne(r => r.VolunteerExaminer).WithMany().HasForeignKey(r => r.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        
    }
}
