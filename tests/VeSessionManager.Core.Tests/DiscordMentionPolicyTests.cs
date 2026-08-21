using Discord;
using VeSessionManager.Core.Discord;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Which mentions a channel post is allowed to resolve — per team (#116).
///
/// <para><b>Why this is an allow-list and not a switch.</b> Every post has gone out with
/// <c>AllowedMentions.None</c>, and that is what makes <c>DiscordMessageText</c>'s decision <i>not</i>
/// to escape markdown safe: a candidate whose name is <c>@everyone</c> cannot ping the server, because
/// no mention in the message resolves. A boolean "allow mentions" hands that back wholesale — and
/// candidate names reach a channel post through <c>{{Subjects}}</c>, so the hostile string is not
/// hypothetical.</para>
///
/// <para>Naming the roles keeps the guarantee: only ids a team deliberately listed resolve, and
/// <c>@everyone</c> is a separate flag that is never set whatever the text says.</para>
/// </summary>
public class DiscordMentionPolicyTests
{
    /// <summary>The default, and what every existing team gets: nothing resolves.</summary>
    [Fact]
    public void NoRolesConfigured_AllowsNothing()
        => Assert.Same(AllowedMentions.None, DiscordMentionPolicy.For([]));

    [Fact]
    public void NullRoles_AllowNothing()
        => Assert.Same(AllowedMentions.None, DiscordMentionPolicy.For(null));

    [Fact]
    public void AConfiguredRole_IsTheOnlyThingAllowed()
    {
        var allowed = DiscordMentionPolicy.For([123UL]);

        Assert.Equal([123UL], allowed.RoleIds);

        // ⚠️ Null AllowedTypes is what restricts resolution to the listed ids. Setting any flag here
        // would widen it — and Everyone is one of those flags.
        Assert.Null(allowed.AllowedTypes);
        Assert.True(allowed.UserIds is null || allowed.UserIds.Count == 0);
    }

    [Fact]
    public void SeveralRoles_AreAllAllowed()
        => Assert.Equal([1UL, 2UL, 3UL], DiscordMentionPolicy.For([1UL, 2UL, 3UL]).RoleIds);

    // ---- Parsing what a team typed -------------------------------------------------------------

    [Theory]
    [InlineData("123", new ulong[] { 123 })]
    [InlineData("123,456", new ulong[] { 123, 456 })]
    [InlineData(" 123 , 456 ", new ulong[] { 123, 456 })]
    [InlineData("123 456", new ulong[] { 123, 456 })]
    [InlineData("123\n456", new ulong[] { 123, 456 })]
    public void RoleIds_ParseFromWhatSomebodyPastes(string stored, ulong[] expected)
        => Assert.Equal(expected, DiscordMentionPolicy.ParseRoleIds(stored));

    /// <summary>Discord's own copy-id gives a bare snowflake, but people paste the mention form too.</summary>
    [Theory]
    [InlineData("<@&123>", new ulong[] { 123 })]
    [InlineData("<@&123>, <@&456>", new ulong[] { 123, 456 })]
    public void TheMentionForm_IsAccepted(string stored, ulong[] expected)
        => Assert.Equal(expected, DiscordMentionPolicy.ParseRoleIds(stored));

    /// <summary>
    /// ⚠️ Anything unparseable is dropped rather than guessed at. A malformed entry that silently
    /// became some other id would ping the wrong room of people.
    /// </summary>
    [Theory]
    [InlineData("everyone")]
    [InlineData("@everyone")]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData(null)]
    public void Rubbish_ParsesToNothing(string? stored)
        => Assert.Empty(DiscordMentionPolicy.ParseRoleIds(stored));

    [Fact]
    public void OneBadEntry_DoesNotDiscardTheGoodOnes()
        => Assert.Equal([123UL], DiscordMentionPolicy.ParseRoleIds("123, nonsense"));

    [Fact]
    public void DuplicateIds_AreCollapsed()
        => Assert.Equal([123UL], DiscordMentionPolicy.ParseRoleIds("123, 123"));
}
