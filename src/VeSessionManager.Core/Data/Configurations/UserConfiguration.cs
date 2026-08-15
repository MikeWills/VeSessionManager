using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="User"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        // See the Candidate block above (#314, L-18).
        b.Property(u => u.MustChangePassword).HasDefaultValue(false);
        b.Property(u => u.ThemePreference).HasDefaultValue(ThemePreference.System);

        b.HasOne(u => u.ManagedByUser).WithMany().HasForeignKey(u => u.ManagedByUserId).OnDelete(DeleteBehavior.Restrict);
        

        // Restrict like every other FK here: a VE record is never hard-deleted out from under a
        // login (see the note at the top of this method).
        b.HasOne(u => u.VolunteerExaminer).WithMany().HasForeignKey(u => u.VolunteerExaminerId).OnDelete(DeleteBehavior.Restrict);

        // One login per VE record. Filtered so the many users with no link do not collide with
        // each other — SQLite treats NULLs as distinct in a unique index, but the filter says so
        // explicitly rather than relying on that, matching how Frn is handled on VolunteerExaminer.
        b.HasIndex(u => u.VolunteerExaminerId).IsUnique().HasFilter("\"VolunteerExaminerId\" IS NOT NULL");
        
    }
}
