using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="Vec"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VecConfiguration : IEntityTypeConfiguration<Vec>
{
    public void Configure(EntityTypeBuilder<Vec> b)
    {
        b.HasIndex(v => v.Name).IsUnique();
        // Two VECs claiming the same ExamTools code would make ingestion's match ambiguous.
        // SQLite treats NULLs as distinct in a unique index, so the many rows that leave this
        // null (code == name) don't collide with each other.
        b.HasIndex(v => v.ExamToolsCode).IsUnique();
        
    }
}
