using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="Candidate"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class CandidateConfiguration : IEntityTypeConfiguration<Candidate>
{
    public void Configure(EntityTypeBuilder<Candidate> b)
    {
        // Declared so a model-built schema (EnsureCreated, i.e. the SQLite tests) matches the
        // migrated one — the identical drift already called out for Team.SquareEnvironment
        // below (#314, L-18). Each value equals the CLR default, so nothing changes for an EF
        // insert; what it fixes is a row inserted OUTSIDE EF hitting a NOT NULL failure on one
        // schema and succeeding on the other.
        //
        // Deliberately NOT applied to every migration default: most of the others are one-time
        // BACKFILL values (TeamId = 1 for the multi-team split, CreatedUtc = a fixed instant)
        // that exist to populate existing rows and would be wrong as ongoing defaults. Identity's
        // own columns are left alone for the same reason — they come from IdentityUser, not from
        // this model.
        b.Property(c => c.FccHoldReason).HasDefaultValue(FccApplicationHoldReason.None);
        b.Property(c => c.FccPaymentStatus).HasDefaultValue(FccApplicationPaymentStatus.Unknown);

        b.HasIndex(c => new { c.SessionId, c.ExamToolsApplicantId }).IsUnique();
        // Applicant Status and the ULS watcher both select by status across every session, and
        // the terminal statuses are the majority — so this is a filter that removes most rows,
        // which is exactly when an index earns its place.
        b.HasIndex(c => c.ApplicationStatus);
        b.HasOne(c => c.Session).WithMany(s => s.Candidates).HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(c => c.ResultMarkedByUser).WithMany().HasForeignKey(c => c.ResultMarkedByUserId).OnDelete(DeleteBehavior.Restrict);
        
    }
}
