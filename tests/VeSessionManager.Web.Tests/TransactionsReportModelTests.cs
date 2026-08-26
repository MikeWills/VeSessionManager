using VeSessionManager.Core.Entities;
using VeSessionManager.Web.Pages.SessionManager;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// <see cref="TransactionsReportModel.BuildRows"/> — the money-math half of the report, tested in
/// isolation from the database, <c>HttpContext</c> and a signed-in user, none of which it needs.
/// </summary>
public class TransactionsReportModelTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static Payment NewPaidPayment(
        string? candidateName = "Roana Glory", string? candidateNameSnapshot = "Roana Glory",
        decimal amount = 15m, decimal? squareAmountPaidUsd = null, DateTime? paidDateUtc = null)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = Now.AddDays(-3),
            DurationMinutes = 60, Team = team, CreatedUtc = Now
        };
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", Session = session, Name = candidateName,
            DateRegisteredUtc = Now
        };
        return new Payment
        {
            Candidate = candidate,
            CandidateNameSnapshot = candidateNameSnapshot,
            Reason = PaymentReason.InitialExam,
            Amount = amount,
            SquareAmountPaidUsd = squareAmountPaidUsd,
            Status = PaymentStatus.Paid,
            PaidDateUtc = paidDateUtc ?? Now.AddDays(-3),
            CreatedUtc = Now.AddDays(-3)
        };
    }

    [Fact]
    public void APaidPaymentWithNoRefund_IsOneRow()
    {
        var payment = NewPaidPayment();

        var rows = TransactionsReportModel.BuildRows([payment], fromUtc: null, toUtc: null);

        var row = Assert.Single(rows);
        Assert.Equal("Payment", row.TypeLabel);
        Assert.Equal("Paid", row.StatusLabel);
        Assert.Equal(15m, row.SignedAmountUsd);
    }

    /// <summary>The snapshot is what this whole page exists for — it must win even when Candidate.Name is still (coincidentally) populated.</summary>
    [Fact]
    public void CandidateNameSnapshot_IsUsedOverTheLiveCandidateName()
    {
        var payment = NewPaidPayment(candidateName: "Live Name", candidateNameSnapshot: "Snapshot Name");

        var row = Assert.Single(TransactionsReportModel.BuildRows([payment], null, null));

        Assert.Equal("Snapshot Name", row.CandidateName);
    }

    /// <summary>A payment row that predates this column — nothing backfills a value nobody captured, so it falls back to whatever Candidate.Name still says.</summary>
    [Fact]
    public void NullSnapshot_FallsBackToLiveCandidateName()
    {
        var payment = NewPaidPayment(candidateName: "Live Name", candidateNameSnapshot: null);

        var row = Assert.Single(TransactionsReportModel.BuildRows([payment], null, null));

        Assert.Equal("Live Name", row.CandidateName);
    }

    /// <summary>Both gone — an old row whose candidate has since been purged. Reads as "—", not a blank or a throw.</summary>
    [Fact]
    public void NoSnapshotAndPurgedCandidate_ShowsDash()
    {
        var payment = NewPaidPayment(candidateName: null, candidateNameSnapshot: null);

        var row = Assert.Single(TransactionsReportModel.BuildRows([payment], null, null));

        Assert.Equal("—", row.CandidateName);
    }

    /// <summary>SquareAmountPaidUsd — what Square actually took — wins over the nominal Amount when they differ (the ARRL youth-rate case).</summary>
    [Fact]
    public void SquareAmountPaidUsd_WinsOverNominalAmountWhenSet()
    {
        var payment = NewPaidPayment(amount: 15m, squareAmountPaidUsd: 5m);

        var row = Assert.Single(TransactionsReportModel.BuildRows([payment], null, null));

        Assert.Equal(5m, row.SignedAmountUsd);
    }

    [Fact]
    public void ACompletedRefund_IsItsOwnRowAndSubtractsFromTheTotal()
    {
        var payment = NewPaidPayment();
        payment.Refunds.Add(new Refund
        {
            SquarePaymentId = "sq-payment-1", AmountUsd = 15m, SquareIdempotencyKey = "key-1",
            Status = RefundStatus.Completed, RequestedByUserId = 1, RequestedUtc = Now.AddDays(-2)
        });

        var rows = TransactionsReportModel.BuildRows([payment], null, null);

        Assert.Equal(2, rows.Count);
        var refundRow = Assert.Single(rows, r => r.TypeLabel == "Refund");
        Assert.Equal("Refunded", refundRow.StatusLabel);
        Assert.Equal(-15m, refundRow.SignedAmountUsd);
    }

    /// <summary>
    /// A refund attempt that hasn't (or won't) return money still shows — this is a record of what
    /// was attempted, not just what settled — but must not count against the total, or the report
    /// would understate money the team actually has.
    /// </summary>
    [Theory]
    [InlineData(RefundStatus.Pending, "Pending at Square")]
    [InlineData(RefundStatus.Submitting, "Pending at Square")]
    [InlineData(RefundStatus.Rejected, "Rejected by Square")]
    [InlineData(RefundStatus.Failed, "Failed")]
    public void ARefundThatHasNotMovedMoney_ShowsButContributesZero(RefundStatus status, string expectedLabel)
    {
        var payment = NewPaidPayment();
        payment.Refunds.Add(new Refund
        {
            SquarePaymentId = "sq-payment-1", AmountUsd = 15m, SquareIdempotencyKey = "key-1",
            Status = status, RequestedByUserId = 1, RequestedUtc = Now.AddDays(-2)
        });

        var refundRow = Assert.Single(TransactionsReportModel.BuildRows([payment], null, null), r => r.TypeLabel == "Refund");

        Assert.Equal(expectedLabel, refundRow.StatusLabel);
        Assert.Equal(0m, refundRow.SignedAmountUsd);
    }

    /// <summary>Partial refunds are independent rows against the same payment, each with its own status.</summary>
    [Fact]
    public void TwoPartialRefunds_AreTwoIndependentRows()
    {
        var payment = NewPaidPayment(amount: 15m);
        payment.Refunds.Add(new Refund
        {
            SquarePaymentId = "sq-1", AmountUsd = 5m, SquareIdempotencyKey = "key-1",
            Status = RefundStatus.Completed, RequestedByUserId = 1, RequestedUtc = Now.AddDays(-2)
        });
        payment.Refunds.Add(new Refund
        {
            SquarePaymentId = "sq-1", AmountUsd = 3m, SquareIdempotencyKey = "key-2",
            Status = RefundStatus.Pending, RequestedByUserId = 1, RequestedUtc = Now.AddDays(-1)
        });

        var rows = TransactionsReportModel.BuildRows([payment], null, null);

        Assert.Equal(3, rows.Count); // 1 payment + 2 refunds
        Assert.Equal(2, rows.Count(r => r.TypeLabel == "Refund"));
    }

    /// <summary>
    /// The payment and its refund are filtered by their own dates independently — a payment from
    /// last month with a refund issued today belongs in "today"'s range for the refund half only.
    /// </summary>
    [Fact]
    public void DateRangeFiltersThePaymentAndItsRefundIndependently()
    {
        var payment = NewPaidPayment(paidDateUtc: Now.AddDays(-30));
        payment.Refunds.Add(new Refund
        {
            SquarePaymentId = "sq-1", AmountUsd = 15m, SquareIdempotencyKey = "key-1",
            Status = RefundStatus.Completed, RequestedByUserId = 1, RequestedUtc = Now
        });

        // A range that covers "today" but not 30 days ago.
        var rows = TransactionsReportModel.BuildRows([payment], fromUtc: Now.AddDays(-1), toUtc: Now.AddDays(1));

        var row = Assert.Single(rows);
        Assert.Equal("Refund", row.TypeLabel);
    }

    [Fact]
    public void OutOfRangePayment_IsExcludedEntirely()
    {
        var payment = NewPaidPayment(paidDateUtc: Now.AddDays(-30));

        var rows = TransactionsReportModel.BuildRows([payment], fromUtc: Now.AddDays(-1), toUtc: Now.AddDays(1));

        Assert.Empty(rows);
    }

    [Fact]
    public void RowsAreOrderedNewestFirst()
    {
        var older = NewPaidPayment(paidDateUtc: Now.AddDays(-10));
        var newer = NewPaidPayment(paidDateUtc: Now.AddDays(-1));

        var rows = TransactionsReportModel.BuildRows([older, newer], null, null);

        Assert.Equal(newer.PaidDateUtc!.Value.ToString("o"), rows[0].DateSortValue);
        Assert.Equal(older.PaidDateUtc!.Value.ToString("o"), rows[1].DateSortValue);
    }
}
