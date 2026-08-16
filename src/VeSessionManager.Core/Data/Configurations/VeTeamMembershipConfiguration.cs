using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VeTeamMembership"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VeTeamMembershipConfiguration : IEntityTypeConfiguration<VeTeamMembership>
{
    public void Configure(EntityTypeBuilder<VeTeamMembership> b)
    {
        b.HasIndex(m => new { m.VolunteerExaminerId, m.TeamId }).IsUnique();
        b.HasOne(m => m.VolunteerExaminer).WithMany(v => v.TeamMemberships).HasForeignKey(m => m.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Team).WithMany().HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Restrict);
        

        // See TeamConfiguration: a default declared only in the migration is invisible to a
        // schema built from the model.
        b.Property(m => m.EmailSubscribed).HasDefaultValue(false);
    }
}
