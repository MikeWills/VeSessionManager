using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Turning an HTML template into something readable in a chat window (#401 PR4).
///
/// <para>The failure this exists to prevent is unglamorous and certain: templates are HTML because
/// every other channel here is email, and posting one raw puts <c>&lt;p&gt;</c> in front of whoever
/// is in the room.</para>
/// </summary>
public class DiscordMessageTextTests
{
    [Fact]
    public void ParagraphsBecomeLineBreaks_AndTagsDisappear()
    {
        var text = DiscordMessageText.FromHtml("<p>Hi Roana,</p><p>See you on Saturday.</p>");

        Assert.Equal("Hi Roana,\n\nSee you on Saturday.", text);
    }

    [Fact]
    public void BoldSurvivesAsDiscordMarkdown()
    {
        Assert.Equal("Session is **tomorrow**", DiscordMessageText.FromHtml("Session is <strong>tomorrow</strong>"));
        Assert.Equal("Session is *soon*", DiscordMessageText.FromHtml("Session is <em>soon</em>"));
    }

    /// <summary>Both halves of a link are kept: Discord auto-links the URL, and the anchor text is usually the sentence that makes it make sense.</summary>
    [Fact]
    public void ALinkKeepsItsLabelAndItsUrl()
    {
        var text = DiscordMessageText.FromHtml("""<a href="https://zoom.us/j/123">Join the session</a>""");

        Assert.Equal("Join the session (https://zoom.us/j/123)", text);
    }

    /// <summary>Otherwise a bare URL renders as "https://x (https://x)".</summary>
    [Fact]
    public void ALinkWhoseTextIsItsUrl_IsNotWrittenTwice()
    {
        var text = DiscordMessageText.FromHtml("""<a href="https://example.org/x">https://example.org/x</a>""");

        Assert.Equal("https://example.org/x", text);
    }

    /// <summary>
    /// An underscore in a URL is common, and escaping it would leave a visible backslash and a dead
    /// link — which is exactly why this converter does not escape Discord markdown. Pinned so the
    /// "obvious" improvement cannot be made without reading the reason.
    /// </summary>
    [Fact]
    public void AUrlWithAnUnderscore_IsLeftAlone()
    {
        var text = DiscordMessageText.FromHtml("""<a href="https://example.org/get_started">Get started</a>""");

        Assert.Equal("Get started (https://example.org/get_started)", text);
    }

    [Fact]
    public void ListItemsBecomeBullets()
    {
        var text = DiscordMessageText.FromHtml("<ul><li>Photo ID</li><li>Your FRN</li></ul>");

        Assert.Contains("• Photo ID", text);
        Assert.Contains("• Your FRN", text);
    }

    [Fact]
    public void EntitiesAreDecoded()
    {
        Assert.Equal("Tam & Roana — \"the pair\"", DiscordMessageText.FromHtml("Tam &amp; Roana &mdash; &quot;the pair&quot;"));
    }

    /// <summary>A team is free to write a Discord-only template in plain text; nothing should happen to it.</summary>
    [Fact]
    public void PlainTextPassesThroughUnchanged()
    {
        Assert.Equal("Two new registrations today.", DiscordMessageText.FromHtml("Two new registrations today."));
    }

    /// <summary>Discord rejects a message over 2000 characters outright, so a long template would fail the post rather than arrive clipped.</summary>
    [Fact]
    public void AnOverlongMessageIsTruncatedRatherThanRejected()
    {
        var text = DiscordMessageText.FromHtml("<p>" + new string('x', 5000) + "</p>");

        Assert.True(text.Length <= DiscordMessageText.MaxLength);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void EmptyInputIsEmptyOutput()
    {
        Assert.Equal("", DiscordMessageText.FromHtml(""));
        Assert.Equal("", DiscordMessageText.FromHtml("   "));
    }

    /// <summary>What the seeded registration template actually looks like, end to end — the closest thing to the real case.</summary>
    [Fact]
    public void ARealisticTemplateComesOutReadable()
    {
        var text = DiscordMessageText.FromHtml("""
            <p>Hi Roana,</p>
            <p>You're registered for a session on <strong>Saturday</strong>.</p>
            <p><a href="https://zoom.us/j/123">Join on Zoom</a></p>
            <ul>
              <li>Photo ID</li>
              <li>Your FRN</li>
            </ul>
            """);

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain(">", text);
        Assert.Contains("**Saturday**", text);
        Assert.Contains("Join on Zoom (https://zoom.us/j/123)", text);
        Assert.Contains("• Photo ID", text);
    }
}
