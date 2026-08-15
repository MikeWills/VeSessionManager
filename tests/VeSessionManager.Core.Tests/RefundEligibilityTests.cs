using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The refund eligibility rule (#375), which two callers depend on agreeing: RefundService, to
/// refuse the call, and the candidate/unmatched pages, to decide whether to offer it. The whole
/// reason it is one type is that those two answers drifting is the failure mode — see #274, where
/// exactly that happened to the youth-program check.
/// </summary>
public class RefundEligibilityTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Refund Refunded(decimal amount, RefundStatus status) => new()
    {
        SquarePaymentId = "sq-payment-1",
        SquareIdempotencyKey = Guid.NewGuid().ToString("N"),
        AmountUsd = amount,
        Status = status
    };

    private static RefundEligibility For(
        decimal original = 15m, DateTime? paidUtc = null, IEnumerable<Refund>? refunds = null,
        bool isPaid = true, string? squarePaymentId = "sq-payment-1") =>
        RefundEligibility.For(isPaid, squarePaymentId, original, paidUtc ?? Now.AddDays(-3), refunds ?? [], Now);

    [Fact]
    public void AFreshFullyPaidPaymentIsRefundableForItsWholeAmount()
    {
        var eligibility = For();

        Assert.True(eligibility.CanRefund);
        Assert.Equal(15m, eligibility.RemainingUsd);
    }

    /// <summary>
    /// The case the partial-refund support exists for: $5 of a $15 payment already returned leaves
    /// $10, not $15 and not nothing.
    /// </summary>
    [Fact]
    public void APartialRefundLeavesTheRestRefundable()
    {
        var eligibility = For(refunds: [Refunded(5m, RefundStatus.Completed)]);

        Assert.True(eligibility.CanRefund);
        Assert.Equal(10m, eligibility.RemainingUsd);
    }

    /// <summary>
    /// Stricter than Square, on purpose. Square would accept a second full refund while the first is
    /// still PENDING — which can be a fortnight — and the buyer would be paid twice.
    /// </summary>
    [Fact]
    public void APendingRefundStillCountsAgainstWhatIsLeft()
    {
        var eligibility = For(refunds: [Refunded(15m, RefundStatus.Pending)]);

        Assert.False(eligibility.CanRefund);
        Assert.Equal(RefundBlocker.FullyRefunded, eligibility.Blocker);
    }

    /// <summary>The other direction: money Square refused never left, so it cannot have consumed any of the refundable balance.</summary>
    [Theory]
    [InlineData(RefundStatus.Rejected)]
    [InlineData(RefundStatus.Failed)]
    public void ARefundSquareRefusedGivesItsAmountBack(RefundStatus status)
    {
        var eligibility = For(refunds: [Refunded(15m, status)]);

        Assert.True(eligibility.CanRefund);
        Assert.Equal(15m, eligibility.RemainingUsd);
    }

    [Fact]
    public void APaymentOverAYearOldIsPastSquaresWindow()
    {
        var eligibility = For(paidUtc: Now.AddDays(-366));

        Assert.False(eligibility.CanRefund);
        Assert.Equal(RefundBlocker.TooOld, eligibility.Blocker);
    }

    /// <summary>The boundary, because "roughly a year" is exactly the kind of limit that gets implemented as 12 months of 30 days.</summary>
    [Fact]
    public void APaymentJustInsideTheWindowIsStillRefundable()
    {
        Assert.True(For(paidUtc: Now.AddDays(-364)).CanRefund);
    }

    /// <summary>
    /// A missing paid date is treated as unknown, not as too old. Refusing locally would block
    /// perfectly refundable rows; Square holds the real date and will refuse them itself if need be.
    /// </summary>
    [Fact]
    public void AnUnknownPaymentDateDoesNotBlockTheRefund()
    {
        Assert.True(For(paidUtc: null).CanRefund);
    }

    [Fact]
    public void APaymentWithNoSquarePaymentIdCannotBeRefundedFromHere()
    {
        var eligibility = For(squarePaymentId: null);

        Assert.False(eligibility.CanRefund);
        Assert.Equal(RefundBlocker.NoSquarePaymentId, eligibility.Blocker);
    }

    [Fact]
    public void AnUnpaidPaymentHasNothingToRefund()
    {
        var eligibility = For(isPaid: false);

        Assert.False(eligibility.CanRefund);
        Assert.Equal(RefundBlocker.NotPaid, eligibility.Blocker);
    }

    /// <summary>Square's own cap. Checked here so the user is told, rather than being handed Square's error.</summary>
    [Fact]
    public void TwentyRefundsAgainstOnePaymentIsSquaresLimit()
    {
        var twenty = Enumerable.Range(0, 20).Select(_ => Refunded(0.01m, RefundStatus.Completed)).ToList();

        var eligibility = For(refunds: twenty);

        Assert.False(eligibility.CanRefund);
        Assert.Equal(RefundBlocker.RefundLimitReached, eligibility.Blocker);
    }
}
