using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="SkippedSession"/> (#440).
/// </summary>
public class SkippedSessionConfiguration : IEntityTypeConfiguration<SkippedSession>
{
    public void Configure(EntityTypeBuilder<SkippedSession> b)
    {
        // One row per session per team, enforced rather than assumed: ingestion runs hourly, so a
        // missed upsert would turn one misconfiguration into hundreds of alerts within a day.
        b.HasIndex(s => new { s.TeamId, s.ExamToolsSessionId }).IsUnique();

        // Cascade: these rows are about a team's feed and mean nothing without the team.
        b.HasOne(s => s.Team).WithMany().HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.Cascade);

        // Bounded because all three come from a third-party feed this app does not control.
        b.Property(s => s.ExamToolsSessionId).HasMaxLength(128).IsRequired();
        b.Property(s => s.VecCode).HasMaxLength(64).IsRequired();
        b.Property(s => s.Title).HasMaxLength(256);
    }
}
