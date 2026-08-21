using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The {{Logo}} placeholder and its upload validation — see docs/email-logo.md.
/// </summary>
public class EmailLogoTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    // Real magic numbers — the validator reads the bytes, not the declared content type, so a
    // fixture of arbitrary filler would be rejected exactly as a spoofed upload would be.
    private static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
    private static byte[] JpegBytes() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, byte[]? logo = null)
    {
        var team = new Team { Name = "Test Team", ExamToolsTeamCode = "TEST" };
        if (logo is not null)
        {
            team.LogoBytes = logo;
            team.LogoContentType = "image/png";
        }

        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static EmailTemplateRenderer CreateRenderer(AppDbContext dbContext) =>
        new(dbContext, NullLogger<EmailTemplateRenderer>.Instance);

    // ---- Rendering -------------------------------------------------------------------------------

    [Fact]
    public async Task LogoPlaceholder_BecomesACidImageTag_AndAttachesTheBytes()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, PngBytes());

        var rendered = await CreateRenderer(dbContext).RenderTextAsync(
            team.Id, "Hi", "<p>{{Logo}}</p><p>Hi {{CandidateName}}</p>",
            new Dictionary<string, string> { ["CandidateName"] = "Ada" }, "RegistrationConfirmation", CancellationToken.None);

        Assert.NotNull(rendered);
        Assert.Contains($"src=\"cid:{InlineImage.TeamLogoContentId}\"", rendered!.Body);
        Assert.NotNull(rendered.InlineLogo);
        Assert.Equal(InlineImage.TeamLogoContentId, rendered.InlineLogo!.ContentId);
        Assert.Equal(PngBytes(), rendered.InlineLogo.Content);
    }

    /// <summary>
    /// The whole point of the encoding exception. If {{Logo}} went through WebUtility.HtmlEncode
    /// like every other body placeholder, the recipient would see the literal text of an img tag.
    /// </summary>
    [Fact]
    public async Task LogoPlaceholder_IsNotHtmlEncoded()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, PngBytes());

        var rendered = await CreateRenderer(dbContext).RenderTextAsync(
            team.Id, "Hi", "<p>{{Logo}}</p>",
            new Dictionary<string, string>(), "RegistrationConfirmation", CancellationToken.None);

        Assert.DoesNotContain("&lt;img", rendered!.Body);
        Assert.Contains("<img", rendered.Body);
    }

    /// <summary>
    /// The exception must stay scoped to {{Logo}}. Every other placeholder traces back to public
    /// registration intake, so this is the guard that a future edit to the raw-substitution branch
    /// cannot quietly widen it.
    /// </summary>
    [Fact]
    public async Task OtherPlaceholders_AreStillHtmlEncoded_EvenAlongsideTheLogo()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, PngBytes());

        var rendered = await CreateRenderer(dbContext).RenderTextAsync(
            team.Id,
            "Hi",
            "<p>{{Logo}}</p><p>{{CandidateName}}</p>",
            new Dictionary<string, string> { ["CandidateName"] = "<script>alert(1)</script>" },
            "RegistrationConfirmation",
            CancellationToken.None);

        Assert.DoesNotContain("<script>", rendered!.Body);
        Assert.Contains("&lt;script&gt;", rendered.Body);
    }

    /// <summary>A template carrying {{Logo}} must stay valid for a team that has not uploaded one — rendering nothing, never the literal token.</summary>
    [Fact]
    public async Task NoLogoUploaded_RendersToNothing_AndAttachesNothing()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var rendered = await CreateRenderer(dbContext).RenderTextAsync(
            team.Id, "Hi", "<p>{{Logo}}</p><p>Hi</p>",
            new Dictionary<string, string>(), "RegistrationConfirmation", CancellationToken.None);

        Assert.DoesNotContain("{{Logo}}", rendered!.Body);
        Assert.DoesNotContain("<img", rendered.Body);
        Assert.Null(rendered.InlineLogo);
    }

    /// <summary>A template that never mentions the logo must not pay the attachment's size on every send.</summary>
    [Fact]
    public async Task TemplateWithoutThePlaceholder_AttachesNothing_EvenWhenTheTeamHasALogo()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, PngBytes());

        var rendered = await CreateRenderer(dbContext).RenderTextAsync(
            team.Id, "Hi", "<p>Hi {{CandidateName}}</p>",
            new Dictionary<string, string> { ["CandidateName"] = "Ada" }, "RegistrationConfirmation", CancellationToken.None);

        Assert.Null(rendered!.InlineLogo);
    }

    // ---- Upload validation -----------------------------------------------------------------------

    private static async Task<(TeamSettingsService Service, Team Team, AppDbContext Db)> CreateServiceAsync()
    {
        var dbContext = CreateContext();
        var team = new Team { Name = "Test Team", ExamToolsTeamCode = "TEST" };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(new User { Id = 1, Name = "Admin", UserName = "admin", Email = "a@example.com" });
        await dbContext.SaveChangesAsync();
        return (new TeamSettingsService(dbContext, new FixedTimeProvider(Now), NullLogger<TeamSettingsService>.Instance), team, dbContext);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public async Task AcceptsPngAndJpeg_AndDerivesTheContentTypeFromTheBytes(string kind)
    {
        var (service, team, db) = await CreateServiceAsync();
        using var _ = db;

        var result = await service.UpdateLogoAsync(team.Id, kind == "png" ? PngBytes() : JpegBytes(), 1, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var saved = await db.Teams.FirstAsync();
        Assert.Equal(kind == "png" ? "image/png" : "image/jpeg", saved.LogoContentType);
        Assert.Equal(Now, saved.LogoUpdatedUtc);
    }

    /// <summary>
    /// A browser-declared Content-Type is attacker-controlled, so the validator never consults it —
    /// this is a file that would claim to be a PNG and is not.
    /// </summary>
    [Fact]
    public async Task RejectsAnythingThatIsNotActuallyAnImage()
    {
        var (service, team, db) = await CreateServiceAsync();
        using var _ = db;

        var svg = System.Text.Encoding.UTF8.GetBytes("<svg onload=\"alert(1)\"></svg>");
        var result = await service.UpdateLogoAsync(team.Id, svg, 1, CancellationToken.None);

        Assert.Equal(TeamActionResult.LogoUnsupportedFormat, result);
        Assert.Null((await db.Teams.FirstAsync()).LogoBytes);
    }

    [Fact]
    public async Task RejectsAnOversizedImage()
    {
        var (service, team, db) = await CreateServiceAsync();
        using var _ = db;

        var huge = new byte[TeamSettingsService.MaxLogoBytes + 1];
        PngBytes().CopyTo(huge, 0);

        Assert.Equal(TeamActionResult.LogoTooLarge, await service.UpdateLogoAsync(team.Id, huge, 1, CancellationToken.None));
    }

    [Fact]
    public async Task NullContentClearsTheLogo()
    {
        var (service, team, db) = await CreateServiceAsync();
        using var _ = db;

        await service.UpdateLogoAsync(team.Id, PngBytes(), 1, CancellationToken.None);
        Assert.Equal(TeamActionResult.Success, await service.UpdateLogoAsync(team.Id, null, 1, CancellationToken.None));

        var saved = await db.Teams.FirstAsync();
        Assert.Null(saved.LogoBytes);
        Assert.Null(saved.LogoContentType);
        Assert.Null(saved.LogoUpdatedUtc);
    }

    /// <summary>Logo is available in every template, so it belongs to Universal rather than to any one Key — keeping ByKey meaning strictly "what the sending service passes in".</summary>
    [Fact]
    public void LogoIsAUniversalPlaceholder_NotAPerKeyOne()
    {
        Assert.Contains("Logo", EmailTemplatePlaceholders.Universal);
        Assert.DoesNotContain("Logo", EmailTemplatePlaceholders.For("RegistrationConfirmation"));
        Assert.Contains("Logo", EmailTemplatePlaceholders.ForEditor("RegistrationConfirmation"));
    }
}
