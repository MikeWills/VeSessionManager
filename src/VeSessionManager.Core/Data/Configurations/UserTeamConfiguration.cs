using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="UserTeam"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class UserTeamConfiguration : IEntityTypeConfiguration<UserTeam>
{
    public void Configure(EntityTypeBuilder<UserTeam> b)
    {
        b.HasKey(ut => new { ut.UserId, ut.TeamId });
        b.HasOne(ut => ut.User).WithMany(u => u.UserTeams).HasForeignKey(ut => ut.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(ut => ut.Team).WithMany(t => t.UserTeams).HasForeignKey(ut => ut.TeamId).OnDelete(DeleteBehavior.Restrict);
        
    }
}
