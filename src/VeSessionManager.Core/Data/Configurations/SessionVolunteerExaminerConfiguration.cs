using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="SessionVolunteerExaminer"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class SessionVolunteerExaminerConfiguration : IEntityTypeConfiguration<SessionVolunteerExaminer>
{
    public void Configure(EntityTypeBuilder<SessionVolunteerExaminer> b)
    {
        b.HasKey(sve => new { sve.SessionId, sve.VolunteerExaminerId });
        b.HasOne(sve => sve.Session).WithMany(s => s.SessionVolunteerExaminers).HasForeignKey(sve => sve.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(sve => sve.VolunteerExaminer).WithMany(v => v.SessionVolunteerExaminers).HasForeignKey(sve => sve.VolunteerExaminerId).OnDelete(DeleteBehavior.Restrict);
        
    }
}
