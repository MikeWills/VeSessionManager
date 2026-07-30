using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class FeeConfigurationTests
{
    [Fact]
    public void RemitToVecAmount_ChargedAmountExceedsRetained_ReturnsDifference()
    {
        var feeConfiguration = new FeeConfiguration { RetainedAmount = 7m };

        Assert.Equal(8m, feeConfiguration.RemitToVecAmount(15m));
    }

    [Fact]
    public void RemitToVecAmount_YouthFeeLessThanRetainedCap_ClampsToZero_DoesNotGoNegative()
    {
        // Real ARRL scenario: $5 youth fee, $7 retained cap — the team keeps the whole $5 and owes
        // the VEC nothing, not a nonsensical -$2.
        var feeConfiguration = new FeeConfiguration { RetainedAmount = 7m };

        Assert.Equal(0m, feeConfiguration.RemitToVecAmount(5m));
    }

    [Fact]
    public void RemitToVecAmount_ChargedAmountEqualsRetained_ReturnsZero()
    {
        var feeConfiguration = new FeeConfiguration { RetainedAmount = 7m };

        Assert.Equal(0m, feeConfiguration.RemitToVecAmount(7m));
    }

    [Fact]
    public void RemitToVecAmount_RetainedAmountNotSet_ReturnsNull()
    {
        var feeConfiguration = new FeeConfiguration { RetainedAmount = null };

        Assert.Null(feeConfiguration.RemitToVecAmount(15m));
    }
}
