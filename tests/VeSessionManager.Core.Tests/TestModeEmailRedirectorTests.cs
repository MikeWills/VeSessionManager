using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class TestModeEmailRedirectorTests
{
    private static readonly EmailMessage Original = new(
        ToAddress: "candidate@example.com",
        FromAddress: "noreply@example.org",
        FromDisplayName: "Test Team",
        ReplyToAddress: "reply@example.org",
        Subject: "Your session is confirmed",
        HtmlBody: "<p>See you there.</p>");

    [Fact]
    public void TestModeDisabled_MessageIsUnchanged_NotRedirected()
    {
        var (message, redirected) = TestModeEmailRedirector.Apply(Original, testModeEnabled: false, overrideEmail: "tester@example.com");

        Assert.False(redirected);
        Assert.Same(Original, message);
    }

    [Fact]
    public void TestModeEnabled_NoOverrideEmailConfigured_FailsOpenToOriginalRecipient_NotRedirected()
    {
        // Defensive fallback for a row somehow in an inconsistent state (enabled but no address) —
        // SystemSettingsService.UpdateAsync should never let this combination be saved, but this is
        // the last line of defense against silently dropping a real email if it ever happens anyway.
        var (message, redirected) = TestModeEmailRedirector.Apply(Original, testModeEnabled: true, overrideEmail: null);

        Assert.False(redirected);
        Assert.Same(Original, message);
    }

    [Fact]
    public void TestModeEnabled_WithOverrideEmail_RedirectsToAddress_KeepsOriginalRecipientVisible()
    {
        var (message, redirected) = TestModeEmailRedirector.Apply(Original, testModeEnabled: true, overrideEmail: "tester@example.com");

        Assert.True(redirected);
        Assert.Equal("tester@example.com", message.ToAddress);
        Assert.Equal("[TEST MODE] Your session is confirmed", message.Subject);
        Assert.Contains("candidate@example.com", message.HtmlBody);
        Assert.Contains("<p>See you there.</p>", message.HtmlBody);
        // From/ReplyTo are left as the real team's addresses — only the recipient is redirected.
        Assert.Equal(Original.FromAddress, message.FromAddress);
        Assert.Equal(Original.ReplyToAddress, message.ReplyToAddress);
    }

    [Fact]
    public void TestModeEnabled_OriginalRecipientWithHtml_IsEncodedInRedirectNotice()
    {
        var original = Original with { ToAddress = "\"<script>alert(1)</script>\"@example.com" };

        var (message, _) = TestModeEmailRedirector.Apply(original, testModeEnabled: true, overrideEmail: "tester@example.com");

        Assert.DoesNotContain("<script>alert(1)</script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
    }
}
