using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="AuditLog"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);

        // Restrict, like the user FK beside it: an audit row must outlive tidying of the thing it
        // describes. Deleting a team is not a supported operation here anyway, and if it ever
        // becomes one, this makes the audit trail an explicit decision rather than collateral.
        b.HasOne(a => a.Team).WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Restrict);

        // The audit page orders by this and nothing else. Retention now exists (#86 part 2,
        // SystemSettings.AuditLogRetentionDays) but is null by default, so the table is still
        // unbounded on most deployments.
        b.HasIndex(a => a.TimestampUtc);

        // Half of every scoped admin's audit query since #86 part 3 — the OR arm that makes
        // background-job entries reachable at all. The other arm goes through the user's team
        // memberships and is served by the User FK index above.
        b.HasIndex(a => a.TeamId);
    }
}
