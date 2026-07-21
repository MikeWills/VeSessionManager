using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class EmailTemplateAdminServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static EmailTemplateAdminService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<(Team Team, User User, EmailTemplate Template)> SeedTemplateAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TEAMA", CreatedUtc = Now };
        var user = new User { Name = "Team Admin", Role = UserRole.TeamAdmin };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var template = new EmailTemplate
        {
            TeamId = team.Id,
            Key = "FelonyDisclosureInstructions",
            Subject = "Old Subject",
            Body = "Old body {{CandidateName}}."
        };
        dbContext.EmailTemplates.Add(template);
        await dbContext.SaveChangesAsync();

        return (team, user, template);
    }

    [Fact]
    public async Task UpdateAsync_ExistingTemplate_UpdatesSubjectAndBody_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var (_, user, template) = await SeedTemplateAsync(dbContext);

        var result = await CreateService(dbContext).UpdateAsync(template.Id, "New Subject", "New body {{CandidateName}}.", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        var updated = await dbContext.EmailTemplates.SingleAsync();
        Assert.Equal("New Subject", updated.Subject);
        Assert.Equal("New body {{CandidateName}}.", updated.Body);
        Assert.Equal(user.Id, updated.UpdatedByUserId);
        Assert.Equal(Now, updated.UpdatedUtc);

        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("EmailTemplateUpdated", audit.Action);
        Assert.Equal(nameof(EmailTemplate), audit.EntityType);
        Assert.Equal(template.Id, audit.EntityId);
    }

    [Fact]
    public async Task UpdateAsync_UnknownTemplate_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Team Admin", Role = UserRole.TeamAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(999, "Subject", "Body", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_ThenRenderAsync_ReflectsTheEditImmediately_NoDeployNeeded()
    {
        await using var dbContext = CreateContext();
        var (team, user, template) = await SeedTemplateAsync(dbContext);

        await CreateService(dbContext).UpdateAsync(template.Id, "Edited Subject", "Edited body for {{CandidateName}}.", user.Id, CancellationToken.None);

        var renderer = new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance);
        var rendered = await renderer.RenderAsync(team.Id, "FelonyDisclosureInstructions", new Dictionary<string, string> { ["CandidateName"] = "Jane Doe" }, CancellationToken.None);

        Assert.NotNull(rendered);
        Assert.Equal("Edited Subject", rendered!.Subject);
        Assert.Equal("Edited body for Jane Doe.", rendered.Body);
    }

    [Fact]
    public async Task FindUnknownPlaceholders_TokenNotInRegistry_IsReported()
    {
        await using var dbContext = CreateContext();

        var unknown = CreateService(dbContext).FindUnknownPlaceholders(
            "FelonyDisclosureInstructions", "Subject with {{CandidateName}}", "Body with {{TotallyMadeUpToken}}");

        var token = Assert.Single(unknown);
        Assert.Equal("TotallyMadeUpToken", token);
    }

    [Fact]
    public async Task FindUnknownPlaceholders_AllTokensKnown_ReturnsEmpty()
    {
        await using var dbContext = CreateContext();

        var unknown = CreateService(dbContext).FindUnknownPlaceholders(
            "FelonyDisclosureInstructions", "Subject with {{CandidateName}}", "Body with {{CandidateName}} again.");

        Assert.Empty(unknown);
    }
}
