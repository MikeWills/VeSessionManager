using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

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
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Test",
            Subject = "Hello {{FirstName}}",
            Body = "<p>Hi {{FirstName}}, your session is {{SessionDate}}.</p>"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "Test",
            new Dictionary<string, string> { ["FirstName"] = "Roana", ["SessionDate"] = "July 24" },
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
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Test",
            Subject = "Hi {{CandidateName}}",
            Body = "<p>Hi {{CandidateName}}, welcome.</p>"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "Test",
            new Dictionary<string, string> { ["CandidateName"] = "<script>alert(1)</script>" },
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
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Test",
            Subject = "Subject",
            Body = "Payment link: {{PaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "Test",
            new Dictionary<string, string> { ["PaymentLinkUrl"] = "" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Payment link: ", result.Body);
    }

    [Fact]
    public async Task UnknownPlaceholder_IsLeftLiteral_NotSilentlyDropped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Test",
            Subject = "Subject",
            Body = "Hi {{Typo}}, welcome."
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "Test",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.NotNull(result);
        // Left as the literal token, not blanked out and not silently sent as if nothing were wrong.
        Assert.Equal("Hi {{Typo}}, welcome.", result.Body);
    }

    [Fact]
    public async Task MissingTemplateKey_ReturnsNull_DoesNotThrow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "DoesNotExist", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task MultiplePlaceholders_SameKey_AllSubstituted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Test",
            Subject = "Subject",
            Body = "{{Name}}, {{Name}} again, and {{Other}}."
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync(team.Id, "Test",
            new Dictionary<string, string> { ["Name"] = "Roana", ["Other"] = "x" },
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
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = teamA.Id, Key = "Test", Subject = "A", Body = "Team A" });
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = teamB.Id, Key = "Test", Subject = "B", Body = "Team B" });
        await dbContext.SaveChangesAsync();

        var resultA = await CreateRenderer(dbContext).RenderAsync(teamA.Id, "Test", new Dictionary<string, string>(), CancellationToken.None);
        var resultB = await CreateRenderer(dbContext).RenderAsync(teamB.Id, "Test", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal("Team A", resultA?.Body);
        Assert.Equal("Team B", resultB?.Body);
    }
}
