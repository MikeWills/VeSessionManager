using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="Payment"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.HasOne(p => p.Candidate).WithMany(c => c.Payments).HasForeignKey(p => p.CandidateId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.RefundRequestedByUser).WithMany().HasForeignKey(p => p.RefundRequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        // The Square webhook's only lookup, and it runs against Square's response deadline: an
        // unindexed scan of every payment ever taken is not something to leave on that path.
        // Not unique — see the note below on nulls, and a refunded/re-created link can repeat.
        b.HasIndex(p => p.SquarePaymentReferenceId);
        b.Property(p => p.Amount).HasPrecision(10, 2);
        b.Property(p => p.SquareAmountPaidUsd).HasPrecision(10, 2);
        // SQLite treats NULLs as distinct in a unique index, so multiple Payments with a null
        // token (the common case — only sessions under a youth-program Vec ever get one) are
        // fine; only a real, generated token collision would violate this.
        b.HasIndex(p => p.YouthConfirmationToken).IsUnique();

        // One InitialExam payment per candidate, enforced by the database (2026-08-03).
        // PaymentGenerationService decides whether to create one by checking
        // "!c.Payments.Any(p => p.Reason == InitialExam)" — a read that the Web process (manual
        // refresh) and the Worker (scheduled tick) can both perform before either one saves,
        // concluding independently that no payment exists. The result was two Unpaid rows, two
        // live Square checkout links, and later two reminder emails for one candidate. Nothing
        // in the schema prevented it.
        //
        // Filtered to InitialExam because a Retest payment legitimately repeats — a candidate
        // may sit (and pay for) several retests. The filter is written from the enum value
        // rather than a hardcoded 0 so it cannot silently drift if the enum is ever renumbered.
        //
        // The index converts an invisible double-charge into a caught constraint violation,
        // which PaymentGenerationService handles per-candidate as "the other process already
        // created it" rather than as an error.
        b.HasIndex(p => new { p.CandidateId, p.Reason })
            .IsUnique()
            .HasFilter($"\"Reason\" = {(int)PaymentReason.InitialExam}");

        // Plain FK lookups — Include(c => c.Payments) and c.Payments.Any(...), which run
        // throughout ingestion, the session detail page and the payment jobs — cannot use the
        // filtered index above: SQLite only considers a partial index when the query's WHERE
        // implies its filter, and none of those mention Reason. Without this they full-scan
        // (#297). An addition, not a replacement — the filtered one is what closes the
        // duplicate-InitialExam race described above.
        b.HasIndex(p => p.CandidateId);
        
    }
}
