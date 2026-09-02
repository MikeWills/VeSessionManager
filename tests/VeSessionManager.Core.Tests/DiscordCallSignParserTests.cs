using VeSessionManager.Core.Discord;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Pulling call-sign-shaped tokens out of a Discord display name (#519 step 2).
///
/// <para><b>This produces candidates, not matches.</b> The shape test is deliberately loose — "Ham2"
/// is call-sign-shaped and nobody's call — because the real filter is the team's own roster: a token
/// only means something if it equals a VE's call sign. Tightening the shape here would buy nothing
/// and would start rejecting real calls.</para>
/// </summary>
public class DiscordCallSignParserTests
{
    [Theory]
    [InlineData("WX0MIK", "WX0MIK")]
    [InlineData("wx0mik", "WX0MIK")]                       // people type their call in lower case
    [InlineData("Mike - WX0MIK", "WX0MIK")]
    [InlineData("Mike (WX0MIK)", "WX0MIK")]
    [InlineData("WX0MIK | Mike", "WX0MIK")]
    [InlineData("[VE] WX0MIK", "WX0MIK")]
    [InlineData("Mike, WX0MIK — Session Manager", "WX0MIK")]
    [InlineData("KF0JZP", "KF0JZP")]
    [InlineData("2E0ABC", "2E0ABC")]                       // a UK call: leading digit, still valid
    public void ACallSignIsFoundWhereverItSits(string displayName, string expected) =>
        Assert.Contains(expected, DiscordCallSignParser.Candidates(displayName));

    /// <summary>
    /// A portable indicator is part of how people write themselves in a server name, and the stored
    /// call sign never carries one — so the base call has to be offered as a candidate in its own
    /// right or "WX0MIK/M" matches nobody.
    /// </summary>
    [Theory]
    [InlineData("WX0MIK/M")]
    [InlineData("WX0MIK/QRP")]
    [InlineData("W1AW/4")]
    public void APortableSuffixStillOffersTheBaseCall(string displayName)
    {
        var candidates = DiscordCallSignParser.Candidates(displayName);
        Assert.Contains(candidates, c => c == displayName.Split('/')[0].ToUpperInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mike")]
    [InlineData("Session Manager")]
    [InlineData("73")]                                     // digits only — no letter, not a call
    public void ANameWithNoCallSignYieldsNothing(string displayName) =>
        Assert.Empty(DiscordCallSignParser.Candidates(displayName));

    /// <summary>
    /// Two call signs in one name is not resolved here — both are returned, and the caller decides.
    /// Silently taking the first would pick a person by string order.
    /// </summary>
    [Fact]
    public void TwoCallSignsBothComeBack()
    {
        var candidates = DiscordCallSignParser.Candidates("WX0MIK and KF0JZP");

        Assert.Contains("WX0MIK", candidates);
        Assert.Contains("KF0JZP", candidates);
    }

    /// <summary>The same token twice is one candidate — "WX0MIK (WX0MIK)" should not read as ambiguous.</summary>
    [Fact]
    public void RepeatsCollapse() =>
        Assert.Single(DiscordCallSignParser.Candidates("WX0MIK (WX0MIK)"));

    /// <summary>
    /// Emoji and non-ASCII decoration are everywhere in Discord names and must not swallow the token
    /// beside them.
    /// </summary>
    [Fact]
    public void DecorationAroundTheCallIsIgnored() =>
        Assert.Contains("WX0MIK", DiscordCallSignParser.Candidates("📻 WX0MIK ⚡"));
}
