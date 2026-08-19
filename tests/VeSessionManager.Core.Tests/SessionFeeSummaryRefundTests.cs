using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A refunded exam fee is not owed to the VEC.
///
/// <para><b>Mike's ruling, 2026-08-19:</b> "When we refund, ARRL does not get that fee. The person
/// has not tested." The remit figure had been counting refunded payments in full since refunds
/// shipped (#375) — deliberately, but on a wrong premise. A refund does not move a payment off
/// <c>Paid</c>, which is correct for its own reasons (otherwise the "unpaid and no link" scan would
/// issue the candidate a fresh checkout link), and the fee summary read <c>Paid</c> as "money the
/// team kept".</para>
///
/// <para><b>Netted, not excluded.</b> The one partial-refund case here is someone who paid the adult
/// fee and turned out to qualify as youth — they <i>did</i> test, so the VEC is owed the youth-rate
/// amount rather than nothing. Subtracting refunds from the charged amount produces that answer and
/// the full-refund answer with one rule.</para>
/// </summary>
public class SessionFeeSummaryRefundTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>ARRL's real shape here: $15 charged, $7 retained, $8 to the VEC; youth rate $5.</summary>
    private static Session SessionWith(params Payment[] payments)
    {
        var vec = new Vec { Name = "ARRL" };
        var fee = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1), FeeCollectionEnabled = true,
            ExamFeeAmount = 15m, RetainedAmount = 7m, YouthExamFeeAmount = 5m
        };
        var session = new Session
        {
            ExamToolsSessionId = "et-1", Title = "Testing", Vec = vec, FeeConfiguration = fee,
            ScheduledStartUtc = Now, DurationMinutes = 120
        };

        foreach (var payment in payments)
        {
            var candidate = new Candidate { Session = session, Name = "Candidate", DateRegisteredUtc = Now };
            candidate.Payments.Add(payment);
            session.Candidates.Add(candidate);
        }

        return session;
    }

    private static Payment Paid(decimal amount, params Refund[] refunds)
    {
        var payment = new Payment { Amount = amount, Status = PaymentStatus.Paid };
        foreach (var refund in refunds)
        {
            payment.Refunds.Add(refund);
        }

        return payment;
    }

    private static Refund Refunded(decimal amount, RefundStatus status = RefundStatus.Completed) => new()
    {
        AmountUsd = amount, Status = status, RequestedUtc = Now,
        SquarePaymentId = "sq-1", SquareIdempotencyKey = "idem-1"
    };

    [Fact]
    public void WithNoRefunds_NothingChanges()
    {
        var summary = SessionWith(Paid(15m), Paid(15m)).GetFeeSummary();

        Assert.Equal(30m, summary.TotalCollected);
        Assert.Equal(16m, summary.TotalRemitToVec);
        Assert.Equal(14m, summary.TotalRetained);
    }

    /// <summary>The whole point: they did not test, so the VEC is owed nothing for them.</summary>
    [Fact]
    public void AFullyRefundedPayment_OwesTheVecNothing()
    {
        var summary = SessionWith(Paid(15m, Refunded(15m))).GetFeeSummary();

        Assert.Equal(0m, summary.TotalCollected);
        Assert.Equal(0m, summary.TotalRemitToVec);
    }

    [Fact]
    public void OneRefundAmongSeveral_OnlyRemovesItsOwn()
    {
        var summary = SessionWith(Paid(15m), Paid(15m, Refunded(15m)), Paid(15m)).GetFeeSummary();

        Assert.Equal(30m, summary.TotalCollected);
        Assert.Equal(16m, summary.TotalRemitToVec);
    }

    /// <summary>
    /// Adult fee refunded down to the youth rate — the only partial refund this team issues. They
    /// tested, so this is not the full-refund case: the $5 they effectively paid is under the $7
    /// retained cap, so the team keeps it and the VEC is owed nothing, which is what the youth rate
    /// already produces on its own.
    /// </summary>
    [Fact]
    public void APartialRefundToTheYouthRate_IsTreatedAsHavingPaidTheYouthRate()
    {
        var summary = SessionWith(Paid(15m, Refunded(10m))).GetFeeSummary();

        Assert.Equal(5m, summary.TotalCollected);
        Assert.Equal(0m, summary.TotalRemitToVec);
        Assert.Equal(5m, summary.TotalRetained);
    }

    /// <summary>
    /// A refund in flight still counts: deciding to refund someone is deciding not to remit for them,
    /// and a card refund is Pending for up to 14 days — long enough to file a session in the middle
    /// of it.
    /// </summary>
    [Theory]
    [InlineData(RefundStatus.Submitting)]
    [InlineData(RefundStatus.Pending)]
    [InlineData(RefundStatus.Completed)]
    public void ARefundThatHasNotFailed_ReducesTheRemit(RefundStatus status)
    {
        var summary = SessionWith(Paid(15m, Refunded(15m, status))).GetFeeSummary();

        Assert.Equal(0m, summary.TotalRemitToVec);
    }

    /// <summary>A refund Square turned down means the money stayed, so it is owed after all.</summary>
    [Theory]
    [InlineData(RefundStatus.Rejected)]
    [InlineData(RefundStatus.Failed)]
    public void ARefundThatFailed_DoesNotReduceTheRemit(RefundStatus status)
    {
        var summary = SessionWith(Paid(15m, Refunded(15m, status))).GetFeeSummary();

        Assert.Equal(15m, summary.TotalCollected);
        Assert.Equal(8m, summary.TotalRemitToVec);
    }

    /// <summary>Refunding more than was charged cannot make the VEC owe the team money.</summary>
    [Fact]
    public void ARefundLargerThanThePayment_ClampsAtZero()
    {
        var summary = SessionWith(Paid(15m, Refunded(20m))).GetFeeSummary();

        Assert.Equal(0m, summary.TotalCollected);
        Assert.Equal(0m, summary.TotalRemitToVec);
    }

    /// <summary>
    /// The flat-override path nets refunds too. Without this, a session retaining a flat total would
    /// remit collected-minus-override on money that had been given back.
    /// </summary>
    [Fact]
    public void TheFlatRetainedOverride_AlsoNetsRefunds()
    {
        var session = SessionWith(Paid(15m), Paid(15m, Refunded(15m)));
        session.RetainedAmountOverride = 5m;

        var summary = session.GetFeeSummary();

        Assert.Equal(15m, summary.TotalCollected);
        Assert.Equal(10m, summary.TotalRemitToVec);
        Assert.Equal(5m, summary.TotalRetained);
    }
}
