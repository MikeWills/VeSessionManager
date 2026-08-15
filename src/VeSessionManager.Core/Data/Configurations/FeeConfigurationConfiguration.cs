using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="FeeConfiguration"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class FeeConfigurationConfiguration : IEntityTypeConfiguration<FeeConfiguration>
{
    public void Configure(EntityTypeBuilder<FeeConfiguration> b)
    {
        b.HasOne(f => f.Vec).WithMany(v => v.FeeConfigurations).HasForeignKey(f => f.VecId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(f => f.CreatedByUser).WithMany().HasForeignKey(f => f.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(f => f.ExamFeeAmount).HasPrecision(10, 2);
        b.Property(f => f.RetainedAmount).HasPrecision(10, 2);
        b.Property(f => f.YouthExamFeeAmount).HasPrecision(10, 2);
        
    }
}
