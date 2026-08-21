using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

// The "missing template key returns null" test that used to live here is gone, and nothing stands
// in its place: message content is now MessageRule.Subject/Body handed straight to RenderTextAsync,
// so there is no row that can be missing and no null for the renderer to return.
public class EmailTemplateRendererTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static EmailTemplateRenderer CreateRenderer(AppDbContext dbContext) =>
        new(dbContext, NullLogger<EmailTemplateRenderer>.Instance);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task KnownPlaceholders_AreSubstitutedInSubjectAndBody()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Hello {{FirstName}}",
            "<p>Hi {{FirstName}}, your session is {{SessionDate}}.</p>",
            new Dictionary<string, string> { ["FirstName"] = "Roana", ["SessionDate"] = "July 24" },
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Hello Roana", result.Subject);
        Assert.Equal("<p>Hi Roana, your session is July 24.</p>", result.Body);
    }

    [Fact]
    public async Task PlaceholderValue_WithHtml_IsEncodedInBody_ButNotInSubject()
    {
        // CandidateName (and similar placeholders) ultimately come from ExamTools' public
        // registration intake — registrant-controlled data. Body is sent as real HTML
        // (SmtpEmailSender's HtmlBody), so an HTML/script-bearing name must not be injected
        // verbatim; Subject is plain text and stays unencoded.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Hi {{CandidateName}}",
            "<p>Hi {{CandidateName}}, welcome.</p>",
            new Dictionary<string, string> { ["CandidateName"] = "<script>alert(1)</script>" },
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("<p>Hi &lt;script&gt;alert(1)&lt;/script&gt;, welcome.</p>", result.Body);
        Assert.Equal("Hi <script>alert(1)</script>", result.Subject);
    }

    [Fact]
    public async Task EmptyStringValue_ForAKnownPlaceholder_SubstitutesToBlank_NoWarning()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Subject",
            "Payment link: {{PaymentLinkUrl}}",
            new Dictionary<string, string> { ["PaymentLinkUrl"] = "" },
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Payment link: ", result.Body);
    }

    [Fact]
    public async Task UnknownPlaceholder_IsLeftLiteral_NotSilentlyDropped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Subject",
            "Hi {{Typo}}, welcome.",
            new Dictionary<string, string>(),
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        // Left as the literal token, not blanked out and not silently sent as if nothing were wrong.
        Assert.Equal("Hi {{Typo}}, welcome.", result.Body);
    }

    [Fact]
    public async Task MultiplePlaceholders_SameKey_AllSubstituted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Subject",
            "{{Name}}, {{Name}} again, and {{Other}}.",
            new Dictionary<string, string> { ["Name"] = "Roana", ["Other"] = "x" },
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Roana, Roana again, and x.", result.Body);
    }

    [Fact]
    public async Task SameKey_DifferentTeams_EachGetsItsOwnTemplate()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext);
        var teamB = await SeedTeamAsync(dbContext);

        var resultA = await CreateRenderer(dbContext).RenderTextAsync(teamA.Id, "A", "Team A", new Dictionary<string, string>(), "Test", CancellationToken.None);
        var resultB = await CreateRenderer(dbContext).RenderTextAsync(teamB.Id, "B", "Team B", new Dictionary<string, string>(), "Test", CancellationToken.None);

        Assert.Equal("Team A", resultA?.Body);
        Assert.Equal("Team B", resultB?.Body);
    }

    // ---- #261: the subject is a mail header, and a header ends at a newline ----

    /// <summary>
    /// Not HTML-encoding the subject is correct — it is plain text. What was missing is control
    /// character stripping. {{CandidateName}} originates in ExamTools' public registration intake,
    /// so a name carrying CR/LF is attacker-controlled input reaching a header builder.
    ///
    /// <para>MimeKit re-encodes headers and is generally not vulnerable, which is why this rates
    /// Low. The point of the test is that the app no longer depends on an undocumented property of
    /// a third-party library for its header safety — and that the dependency is now pinned either
    /// way, rather than assumed.</para>
    /// </summary>
    [Fact]
    public async Task SubjectPlaceholder_HasLineBreaksStripped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Session for {{FirstName}}",
            "<p>Hi</p>",
            new Dictionary<string, string> { ["FirstName"] = "Roana\r\nBcc: victim@example.org" },
            "Test",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain('\r', result.Subject);
        Assert.DoesNotContain('\n', result.Subject);
        // Readable rather than mangled: CR dropped, LF becomes a space.
        Assert.Equal("Session for Roana Bcc: victim@example.org", result.Subject);
    }

    /// <summary>
    /// The subject must stay plain text — HTML-encoding it would render "O'Brien" as "O&amp;#39;Brien"
    /// in the recipient's inbox list. Stripping line breaks must not turn into encoding.
    /// </summary>
    [Fact]
    public async Task SubjectPlaceholder_IsNotHtmlEncoded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "For {{FirstName}}",
            "<p>Hi</p>",
            new Dictionary<string, string> { ["FirstName"] = "Ada O'Brien & Co" },
            "Test",
            CancellationToken.None);

        Assert.Equal("For Ada O'Brien & Co", result!.Subject);
    }

    /// <summary>
    /// The Logo placeholder is deliberately raw HTML in the body, so the strip added for the subject
    /// must not reach it. An early version of the fix applied it to every "raw" case, which included
    /// this one.
    ///
    /// <para>Worth recording what the test found: a caller cannot inject through {{Logo}} at all —
    /// the renderer overwrites whatever was passed with a tag it builds itself. The exemption is
    /// narrower than it looks, which is why it is safe.</para>
    /// </summary>
    [Fact]
    public async Task LogoPlaceholder_StaysRawHtmlInTheBody()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.LogoBytes = [1, 2, 3];
        team.LogoContentType = "image/png";
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Hi",
            "<div>{{Logo}}</div>",
            new Dictionary<string, string>(),
            "Test",
            CancellationToken.None);

        // Live markup, not &lt;img …&gt;.
        Assert.Contains("<img src=\"cid:", result!.Body);
        Assert.DoesNotContain("&lt;img", result.Body);
    }

    /// <summary>The caller cannot override it — the renderer's own value wins.</summary>
    [Fact]
    public async Task LogoPlaceholder_CannotBeSuppliedByTheCaller()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderTextAsync(team.Id,
            "Hi",
            "<div>{{Logo}}</div>",
            new Dictionary<string, string> { ["Logo"] = "<script>alert(1)</script>" },
            "Test",
            CancellationToken.None);

        // No logo uploaded, so it renders empty — and the caller's value never appears.
        Assert.Equal("<div></div>", result!.Body);
    }
}
