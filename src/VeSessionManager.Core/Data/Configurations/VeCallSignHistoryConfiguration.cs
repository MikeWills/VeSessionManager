using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeCallSignHistory"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeCallSignHistoryConfiguration : IEntityTypeConfiguration<VeCallSignHistory>
{
    public void Configure(EntityTypeBuilder<VeCallSignHistory> b)
    {
        // Not unique: a call sign can legitimately appear twice — released by one person and
        // later reissued to another — and this table is the record of that, not a constraint on it.
        b.HasIndex(h => h.CallSign);
        b.HasOne(h => h.VolunteerExaminer).WithMany(v => v.CallSignHistory).HasForeignKey(h => h.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        
    }
}
