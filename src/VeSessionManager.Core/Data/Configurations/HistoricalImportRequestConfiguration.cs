using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="HistoricalImportRequest"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class HistoricalImportRequestConfiguration : IEntityTypeConfiguration<HistoricalImportRequest>
{
    public void Configure(EntityTypeBuilder<HistoricalImportRequest> b)
    {
        b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.RequestedByUser).WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        // The Worker's only query is "oldest Pending", and the page's is "this team's requests".
        b.HasIndex(r => new { r.Status, r.RequestedUtc });
        b.HasIndex(r => r.TeamId);
        
    }
}
