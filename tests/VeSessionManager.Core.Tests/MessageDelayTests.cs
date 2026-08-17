using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The days-on-the-form, hours-in-the-column boundary (#401). Small, but it is the only arithmetic
/// standing between "half a day before the session" and a column that counts hours — and the rounding
/// direction is the sort of thing a later refactor changes without noticing.
/// </summary>
public class MessageDelayTests
{
    [Theory]
    [InlineData(0.5, 12)]
    [InlineData(1, 24)]
    [InlineData(1.5, 36)]
    [InlineData(5, 120)]
    [InlineData(10, 240)]
    [InlineData(365, 8760)]
    public void WholeAndHalfDaysBecomeHours(double days, int expectedHours) =>
        Assert.Equal(expectedHours, MessageDelay.ToHours((decimal)days));

    /// <summary>
    /// Refused rather than rounded. Silently turning 0.3 into seven hours would be a rule firing at a
    /// moment nobody chose, and the box would read back 0.5 as if it had always said that.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.75)]
    [InlineData(1.1)]
    public void FinerThanHalfADayIsRefused(double days) =>
        Assert.Null(MessageDelay.ToHours((decimal)days));

    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(-1)]
    [InlineData(366)]
    public void OutOfRangeIsRefused(double days) =>
        Assert.Null(MessageDelay.ToHours((decimal)days));

    /// <summary>Null is "this trigger has no delay", which is not the same as a delay of zero — it must survive the round trip.</summary>
    [Fact]
    public void NullInNullOut()
    {
        Assert.Null(MessageDelay.ToHours(null));
        Assert.Null(MessageDelay.ToDays(null));
    }

    [Theory]
    [InlineData(12, 0.5)]
    [InlineData(24, 1)]
    [InlineData(120, 5)]
    [InlineData(240, 10)]
    public void HoursComeBackAsDays(int hours, double expectedDays) =>
        Assert.Equal((decimal)expectedDays, MessageDelay.ToDays(hours));

    /// <summary>A value predating the day field (or hand-edited) shows to its nearest half rather than blank.</summary>
    [Theory]
    [InlineData(18, 1)]
    [InlineData(40, 1.5)]
    public void AnOddStoredValueShowsToTheNearestHalf(int hours, double expectedDays) =>
        Assert.Equal((decimal)expectedDays, MessageDelay.ToDays(hours));

    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(1, "1")]
    [InlineData(365, "365")]
    public void FormatDropsTrailingZeroes(double days, string expected) =>
        Assert.Equal(expected, MessageDelay.Format((decimal)days));

    /// <summary>The list column and the form must agree, or a rule reads back in a unit it was not written in.</summary>
    [Theory]
    [InlineData(null, "immediately")]
    [InlineData(12, "half a day")]
    [InlineData(24, "1 day")]
    [InlineData(36, "1½ days")]
    [InlineData(120, "5 days")]
    [InlineData(7, "7 hours")]
    public void DescribeHoursReadsInDays(int? hours, string expected) =>
        Assert.Equal(expected, MessageTriggerLabels.DescribeHours(hours));
}
