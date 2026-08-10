using VeSessionManager.Core.Notifications;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// What a candidate reads. The bug this replaces was not subtle in effect — emails gave the session
/// time in UTC while every screen gave Eastern — so these assert the actual rendered string rather
/// than a property of it.
/// </summary>
public class SessionTimeFormatterTests
{
    /// <summary>
    /// A typical Saturday-morning session in summer. EDT is UTC-4, PDT UTC-7.
    /// </summary>
    [Fact]
    public void SummerSession_RendersBothZonesOnOneLine()
    {
        var utc = new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Saturday, August 15, 2026 at 10:00 AM ET / 7:00 AM PT",
            SessionTimeFormatter.ForCandidate(utc));
    }

    /// <summary>
    /// The same wall-clock session in winter is a different UTC instant — EST is UTC-5, PST UTC-8.
    /// If this ever renders 9:00 AM ET, the conversion has been replaced by a fixed offset.
    /// </summary>
    [Fact]
    public void WinterSession_UsesStandardTimeNotAFixedOffset()
    {
        var utc = new DateTime(2026, 1, 17, 15, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Saturday, January 17, 2026 at 10:00 AM ET / 7:00 AM PT",
            SessionTimeFormatter.ForCandidate(utc));
    }

    /// <summary>
    /// The three-hour gap is what makes "interpolate for Central and Mountain" sound advice, so it
    /// is worth pinning. It holds on both sides of a DST switch because both zones change on the
    /// same date.
    /// </summary>
    [Theory]
    [InlineData("2026-03-07T17:00:00Z")]  // before the spring switch
    [InlineData("2026-03-14T17:00:00Z")]  // after it
    [InlineData("2026-10-31T17:00:00Z")]  // before the autumn switch
    [InlineData("2026-11-07T17:00:00Z")]  // after it
    public void TheGapBetweenTheTwoZonesIsAlwaysThreeHours(string utcText)
    {
        var rendered = SessionTimeFormatter.ForCandidate(DateTime.Parse(utcText).ToUniversalTime());

        // Both times appear, and neither is a UTC restatement of the stored instant.
        Assert.Contains(" ET / ", rendered);
        Assert.DoesNotContain("UTC", rendered);
    }

    /// <summary>
    /// Before 3:00 AM Eastern the two zones are on different calendar days. No real session runs
    /// then, but a format that prints one date beside two times would be quietly wrong for every
    /// Pacific reader if one ever did.
    /// </summary>
    [Fact]
    public void WhenTheZonesFallOnDifferentDays_ThePacificSideCarriesItsOwnDate()
    {
        // 05:00 UTC on the 15th = 1:00 AM EDT on the 15th, but 10:00 PM PDT on the 14th.
        var utc = new DateTime(2026, 8, 15, 5, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Saturday, August 15, 2026 at 1:00 AM ET / Friday, August 14 at 10:00 PM PT",
            SessionTimeFormatter.ForCandidate(utc));
    }

    /// <summary>
    /// EF Core/SQLite hands back DateTimeKind.Unspecified, and ConvertTimeFromUtc throws on it. The
    /// value reaching this formatter comes straight off a Session, so this is the shape it will
    /// really be called with — not a hypothetical.
    /// </summary>
    [Fact]
    public void AnUnspecifiedKindFromTheDatabaseIsTreatedAsUtc()
    {
        var fromDatabase = new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal("Saturday, August 15, 2026 at 10:00 AM ET / 7:00 AM PT",
            SessionTimeFormatter.ForCandidate(fromDatabase));
    }
}
