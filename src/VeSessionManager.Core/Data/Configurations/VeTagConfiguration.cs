using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeTag"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeTagConfiguration : IEntityTypeConfiguration<VeTag>
{
    public void Configure(EntityTypeBuilder<VeTag> b)
    {
        b.HasIndex(t => new { t.TeamId, t.Name }).IsUnique();
        b.HasOne(t => t.Team).WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.Cascade);

        // One Discord role means one tag, per team (#519). Per team for the same reason the name
        // index is: two teams can share a Discord server and each map its roles to their own words.
        //
        // Unfiltered on purpose. SQLite treats NULLs in a unique index as distinct, so any number of
        // tags may stay unmapped — which is the normal case, and the thing that would break loudly if
        // it were not true (a team could hold exactly one unmapped tag). EF InMemory enforces no
        // unique index at all, so VeTagDiscordRoleTests pins both halves against real SQLite.
        b.HasIndex(t => new { t.TeamId, t.DiscordRoleId }).IsUnique();
    }
}
