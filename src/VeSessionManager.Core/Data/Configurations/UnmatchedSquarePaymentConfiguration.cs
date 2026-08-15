using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="UnmatchedSquarePayment"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class UnmatchedSquarePaymentConfiguration : IEntityTypeConfiguration<UnmatchedSquarePayment>
{
    public void Configure(EntityTypeBuilder<UnmatchedSquarePayment> b)
    {
        // Guards against a duplicate row for the same order id (e.g. a Square webhook
        // redelivery arriving before a human resolves the first one) — see
        // SquarePaymentMatchingService.HandleUnmatchedOrderAsync.
        b.HasIndex(u => new { u.TeamId, u.SquareOrderId }).IsUnique();
        b.HasOne(u => u.Team).WithMany().HasForeignKey(u => u.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(u => u.ResolvedByUser).WithMany().HasForeignKey(u => u.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(u => u.MatchedPayment).WithMany().HasForeignKey(u => u.MatchedPaymentId).OnDelete(DeleteBehavior.Restrict);
        b.Property(u => u.AmountUsd).HasPrecision(10, 2);
        
    }
}
