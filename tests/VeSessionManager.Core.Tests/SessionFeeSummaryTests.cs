using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>Pure in-memory tests for Session.GetFeeSummary — no DB needed, same style as FeeConfigurationTests.</summary>
public class SessionFeeSummaryTests
{
    private static Session BuildSession(decimal retainedAmount, decimal? retainedAmountOverride, params decimal[] paidAmounts)
    {
        var feeConfiguration = new FeeConfiguration { RetainedAmount = retainedAmount };
        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "Test Session",
            FeeConfiguration = feeConfiguration,
            RetainedAmountOverride = retainedAmountOverride
        };

        var candidate = new Candidate { Name = "Test Candidate", DateRegisteredUtc = DateTime.UtcNow, Session = session };
        foreach (var amount in paidAmounts)
        {
            candidate.Payments.Add(new Payment { Amount = amount, Status = PaymentStatus.Paid });
        }

        session.Candidates.Add(candidate);
        return session;
    }

    [Fact]
    public void NoOverride_SumsPerCandidateDefaultAcrossAllPaidPayments()
    {
        // Two candidates, standard $15 fee, $7 retained cap each — matches the real per-candidate default.
        var session = BuildSession(retainedAmount: 7m, retainedAmountOverride: null, paidAmounts: [15m, 15m]);

        var summary = session.GetFeeSummary();

        Assert.Equal(30m, summary.TotalCollected);
        Assert.Equal(14m, summary.TotalRetained); // $7 + $7
        Assert.Equal(16m, summary.TotalRemitToVec); // $8 + $8
    }

    [Fact]
    public void NoOverride_YouthFeeUnderRetainedCap_KeepsWholeFeeAcrossTheSum()
    {
        var session = BuildSession(retainedAmount: 7m, retainedAmountOverride: null, paidAmounts: [15m, 5m]); // regular + youth

        var summary = session.GetFeeSummary();

        Assert.Equal(20m, summary.TotalCollected);
        Assert.Equal(12m, summary.TotalRetained); // $7 regular + $5 youth (clamped, not $7)
        Assert.Equal(8m, summary.TotalRemitToVec); // $8 regular + $0 youth
    }

    [Fact]
    public void WithOverride_UsesFlatSessionTotal_NotPerCandidateMath()
    {
        // Real scenario: 50 candidates worth of fees collected, but only $20 of real session
        // expenses — the team doesn't want to compute a per-candidate figure across every payment.
        var paidAmounts = Enumerable.Repeat(15m, 50).ToArray();
        var session = BuildSession(retainedAmount: 7m, retainedAmountOverride: 20m, paidAmounts: paidAmounts);

        var summary = session.GetFeeSummary();

        Assert.Equal(750m, summary.TotalCollected);
        Assert.Equal(20m, summary.TotalRetained);
        Assert.Equal(730m, summary.TotalRemitToVec);
    }

    [Fact]
    public void WithOverride_ExceedsTotalCollected_ClampsRemitToZero_DoesNotGoNegative()
    {
        var session = BuildSession(retainedAmount: 7m, retainedAmountOverride: 100m, paidAmounts: [15m, 15m]); // $30 collected, $100 override

        var summary = session.GetFeeSummary();

        Assert.Equal(30m, summary.TotalCollected);
        Assert.Equal(0m, summary.TotalRemitToVec);
        Assert.Equal(30m, summary.TotalRetained); // can't retain more than was actually collected
    }

    [Fact]
    public void UnpaidPayments_AreExcludedFromTotals()
    {
        var session = BuildSession(retainedAmount: 7m, retainedAmountOverride: null, paidAmounts: [15m]);
        session.Candidates[0].Payments.Add(new Payment { Amount = 15m, Status = PaymentStatus.Unpaid });

        var summary = session.GetFeeSummary();

        Assert.Equal(15m, summary.TotalCollected); // the Unpaid payment doesn't count
    }
}
