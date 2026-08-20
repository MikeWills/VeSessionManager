using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="CandidateUlsHistoryEntry"/> (#195).
/// </summary>
public class CandidateUlsHistoryEntryConfiguration : IEntityTypeConfiguration<CandidateUlsHistoryEntry>
{
    public void Configure(EntityTypeBuilder<CandidateUlsHistoryEntry> b)
    {
        // Every read is "this candidate's timeline, oldest first" — there is no other query shape.
        b.HasIndex(e => new { e.CandidateId, e.LogDateUtc });

        // Cascade: an entry is about a candidate and means nothing without one. Candidate rows
        // survive a PII purge (it nulls fields, it does not delete), so this only fires on a genuine
        // delete — matching CandidateEmailSend.
        b.HasOne(e => e.Candidate).WithMany(c => c.UlsHistory).HasForeignKey(e => e.CandidateId).OnDelete(DeleteBehavior.Cascade);

        // Bounded because both come from a third-party mirror this app does not control. The codes
        // are six characters and the descriptions a short sentence; the caps are headroom, not a
        // guess at the real maximum.
        b.Property(e => e.Code).HasMaxLength(32).IsRequired();
        b.Property(e => e.CodeText).HasMaxLength(256);
    }
}
