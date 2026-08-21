using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Sub-day delays, for #116 — a VE reminder "less than 1 hour out".
///
/// <para>The field was denominated in days with a half-day step, which put a <b>12-hour floor</b> on
/// anything a team could set. That was a deliberate choice, not an oversight: <c>MessageDelay</c>'s own
/// remarks say an odd number of hours "cannot be written in this unit without lying about it, and a
/// form that silently turns 0.3 into 7 hours is worse than one that says no".</para>
///
/// <para>So the unit moves rather than the precision: a delay is a number <i>and</i> a unit, and hours
/// are expressible exactly. The stored column is unchanged — it was always hours.</para>
/// </summary>
public class MessageDelayUnitTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(23, 23)]
    public void Hours_ConvertExactly(decimal value, int expected)
        => Assert.Equal(expected, MessageDelay.ToHours(value, MessageDelayUnit.Hours));

    /// <summary>The case the whole change exists for.</summary>
    [Fact]
    public void OneHour_IsNowSettable()
        => Assert.Equal(1, MessageDelay.ToHours(1, MessageDelayUnit.Hours));

    /// <summary>Days keep working exactly as before — halves in, whole hours out.</summary>
    [Theory]
    [InlineData(0.5, 12)]
    [InlineData(1, 24)]
    [InlineData(5, 120)]
    public void Days_AreUnchanged(decimal value, int expected)
        => Assert.Equal(expected, MessageDelay.ToHours(value, MessageDelayUnit.Days));

    /// <summary>
    /// A fractional hour is refused rather than rounded, for the same reason a third of a day always
    /// was: the stored column is whole hours, and silently turning 1.5 into 1 is a rule that fires at a
    /// time nobody chose.
    /// </summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(0.5)]
    public void AFractionalHour_IsRefused(decimal value)
        => Assert.Null(MessageDelay.ToHours(value, MessageDelayUnit.Hours));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8761)]
    public void HoursOutOfRange_AreRefused(decimal value)
        => Assert.Null(MessageDelay.ToHours(value, MessageDelayUnit.Hours));

    /// <summary>A year, the same ceiling the column and the service already enforce.</summary>
    [Fact]
    public void TheHoursCeiling_MatchesTheDaysCeiling()
        => Assert.Equal(365 * 24, MessageDelay.ToHours(365 * 24, MessageDelayUnit.Hours));

    /// <summary>Null in, null out — a state trigger has no delay, which differs from a delay of zero.</summary>
    [Fact]
    public void Null_StaysNull()
        => Assert.Null(MessageDelay.ToHours(null, MessageDelayUnit.Hours));

    // ---- Choosing the unit to show ------------------------------------------------------------

    /// <summary>
    /// A stored value comes back in the unit that reads naturally: whole days as days, anything else
    /// as hours. 36 hours is a day and a half and shows as 1.5 days; 1 hour has no honest day form.
    /// </summary>
    [Theory]
    [InlineData(24, 1, MessageDelayUnit.Days)]
    [InlineData(120, 5, MessageDelayUnit.Days)]
    [InlineData(36, 1.5, MessageDelayUnit.Days)]
    [InlineData(12, 0.5, MessageDelayUnit.Days)]
    [InlineData(1, 1, MessageDelayUnit.Hours)]
    [InlineData(6, 6, MessageDelayUnit.Hours)]
    [InlineData(23, 23, MessageDelayUnit.Hours)]
    public void ForDisplay_PicksTheNaturalUnit(int hours, decimal expectedValue, MessageDelayUnit expectedUnit)
    {
        var (value, unit) = MessageDelay.ForDisplay(hours)!.Value;

        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedUnit, unit);
    }

    /// <summary>Whatever unit it comes back in, it must convert back to the same number of hours.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    [InlineData(120)]
    [InlineData(8760)]
    public void ForDisplay_RoundTrips(int hours)
    {
        var (value, unit) = MessageDelay.ForDisplay(hours)!.Value;

        Assert.Equal(hours, MessageDelay.ToHours(value, unit));
    }

    [Fact]
    public void ForDisplay_OfNull_IsNull()
        => Assert.Null(MessageDelay.ForDisplay(null));
}
