using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="CandidateEmailSend"/> (#144).
/// </summary>
public class CandidateEmailSendConfiguration : IEntityTypeConfiguration<CandidateEmailSend>
{
    public void Configure(EntityTypeBuilder<CandidateEmailSend> b)
    {
        // Every read is "what has this candidate had, most recent first" — the history modal and the
        // compose screen's already-had-one column.
        b.HasIndex(s => new { s.CandidateId, s.SentUtc });

        // Cascade: these rows are about a candidate and mean nothing without one. Candidate rows
        // survive a PII purge (the purge nulls fields, it does not delete), so this only fires on a
        // genuine delete.
        b.HasOne(s => s.Candidate).WithMany(c => c.EmailSends).HasForeignKey(s => s.CandidateId).OnDelete(DeleteBehavior.Cascade);

        // Restrict, matching every other user-attributed row here: a user who sent mail cannot be
        // deleted out from under the record of having sent it.
        b.HasOne(s => s.SentByUser).WithMany().HasForeignKey(s => s.SentByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
