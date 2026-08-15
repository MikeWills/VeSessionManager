using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeSelfServiceToken"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeSelfServiceTokenConfiguration : IEntityTypeConfiguration<VeSelfServiceToken>
{
    public void Configure(EntityTypeBuilder<VeSelfServiceToken> b)
    {
        // Unique: a presented token resolves to exactly one row or none. A collision would be a
        // 256-bit coincidence, but a unique index turns "cannot happen" into "cannot be stored".
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasOne(t => t.VolunteerExaminer).WithMany().HasForeignKey(t => t.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        
    }
}
