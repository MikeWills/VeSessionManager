using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>Model configuration for <see cref="Refund"/> (#375).</summary>
public class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.RequestedByUser).WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);

        // Restrict, not Cascade, on both sources: a refund is a financial record and must outlive
        // any tidying of the thing it came from. SessionActionService.DeleteSessionAsync already
        // reports a Blocked result when an unmatched payment still references a payment it is
        // deleting (see ActionOutcomes.DeleteSession); this puts refunds behind the same wall
        // rather than letting a session delete quietly take the record of returned money with it.
        b.HasOne(r => r.Payment).WithMany(p => p.Refunds).HasForeignKey(r => r.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.UnmatchedSquarePayment).WithMany(u => u.Refunds)
            .HasForeignKey(r => r.UnmatchedSquarePaymentId).OnDelete(DeleteBehavior.Restrict);

        b.Property(r => r.AmountUsd).HasPrecision(10, 2);

        // Square caps the key at 45 characters. Ours is a 32-character GUID, so this is headroom
        // rather than a limit anything reaches — it exists so a future change to the key format
        // fails here instead of at Square, on a call that has already been made once.
        b.Property(r => r.SquareIdempotencyKey).HasMaxLength(45);

        // The retry lookup: "is there already a refund in flight for this key?" runs before every
        // Square call. Unique because two rows sharing a key would mean this app had lost track of
        // which refund it was retrying, and Square would answer both with the same refund.
        b.HasIndex(r => r.SquareIdempotencyKey).IsUnique();

        // "What has already been refunded against this Square payment?" — asked on every refund to
        // compute the remaining refundable amount, and on every render of a payment.
        b.HasIndex(r => r.SquarePaymentId);

        // The status job's scan: unsettled rows only, which is nearly always none of them.
        b.HasIndex(r => r.SettledUtc);

        // Exactly one source, enforced by the database rather than by the two services that write
        // these agreeing to be careful. A row with neither is unreadable (nothing to render it
        // against); a row with both claims one Square payment is two different things.
        b.ToTable(t => t.HasCheckConstraint(
            "CK_Refund_ExactlyOneSource",
            "(\"PaymentId\" IS NULL) <> (\"UnmatchedSquarePaymentId\" IS NULL)"));
    }
}
