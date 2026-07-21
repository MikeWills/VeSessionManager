using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class TeamSettingsServiceTests
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

    private static TeamSettingsService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<User> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "TEAMA")
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task CreateAsync_NewName_CreatesTeamAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, team) = await CreateService(dbContext).CreateAsync("New Team", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.NotNull(team);
        Assert.Equal("New Team", team!.Name);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("TeamCreated", audit.Action);
        Assert.Equal(nameof(Team), audit.EntityType);
        Assert.Equal(team.Id, audit.EntityId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsDuplicateName()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        await SeedTeamAsync(dbContext, "TEAMA");

        var (result, team) = await CreateService(dbContext).CreateAsync("TEAMA", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.DuplicateName, result);
        Assert.Null(team);
    }

    [Fact]
    public async Task UpdateExamToolsAsync_SetsNonSecretFieldsAndSecretWhenProvided()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateExamToolsAsync(team.Id, "WX0MIK", "admin", "secret-password", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("WX0MIK", updated.ExamToolsTeamCode);
        Assert.Equal("admin", updated.ExamToolsUsername);
        Assert.Equal("secret-password", updated.ExamToolsPassword);
    }

    [Fact]
    public async Task UpdateExamToolsAsync_NullPassword_LeavesExistingPasswordUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.ExamToolsPassword = "original-secret";
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateExamToolsAsync(team.Id, "WX0MIK", "admin", null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-secret", updated.ExamToolsPassword);
        Assert.Equal("WX0MIK", updated.ExamToolsTeamCode);
    }

    [Fact]
    public async Task UpdateExamToolsAsync_UnknownTeam_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var result = await CreateService(dbContext).UpdateExamToolsAsync(999, "WX0MIK", "admin", "secret", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateZoomAsync_NullClientSecret_LeavesExistingSecretUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.ZoomClientSecret = "original-zoom-secret";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateZoomAsync(team.Id, "acct", "client-id", null, "me", user.Id, CancellationToken.None);

        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-zoom-secret", updated.ZoomClientSecret);
        Assert.Equal("client-id", updated.ZoomClientId);
    }

    [Fact]
    public async Task UpdateDiscordAsync_SetsGuildId_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateDiscordAsync(team.Id, 1323140214008578111UL, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal(1323140214008578111UL, updated.DiscordGuildId);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("TeamDiscordSettingsUpdated", audit.Action);
    }

    [Fact]
    public async Task UpdateSquareAsync_NullSecrets_LeaveExistingSecretsUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.SquareAccessToken = "original-token";
        team.SquareWebhookSignatureKey = "original-key";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateSquareAsync(team.Id, null, "loc-1", null, "https://host/webhooks/square/1", user.Id, CancellationToken.None);

        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-token", updated.SquareAccessToken);
        Assert.Equal("original-key", updated.SquareWebhookSignatureKey);
        Assert.Equal("loc-1", updated.SquareLocationId);
        Assert.Equal("https://host/webhooks/square/1", updated.SquareWebhookNotificationUrl);
    }

    [Fact]
    public async Task UpdateSmtpAsync_NullPassword_LeavesExistingPasswordUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.SmtpPassword = "original-smtp-secret";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateSmtpAsync(team.Id, "smtp.mailgun.org", 587, "postmaster@example.org", null, true, user.Id, CancellationToken.None);

        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-smtp-secret", updated.SmtpPassword);
        Assert.Equal("smtp.mailgun.org", updated.SmtpHost);
        Assert.Equal(587, updated.SmtpPort);
        Assert.True(updated.SmtpUseStartTls);
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_NoExistingRow_CreatesOne()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateEmailSettingsAsync(
            team.Id, "noreply@example.org", "VE Team", "reply@example.org", "https://example.org/privacy", "admin@example.org", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var settings = await dbContext.EmailSettings.SingleAsync();
        Assert.Equal(team.Id, settings.TeamId);
        Assert.Equal("noreply@example.org", settings.FromAddress);
        Assert.Equal(user.Id, settings.UpdatedByUserId);
        Assert.Equal(Now, settings.UpdatedUtc);
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_ExistingRow_UpdatesInPlace_DoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "old@example.org",
            ReplyToAddress = "old-reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "old-admin@example.org"
        });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateEmailSettingsAsync(
            team.Id, "new@example.org", null, "new-reply@example.org", "https://example.org/privacy", "new-admin@example.org", user.Id, CancellationToken.None);

        var settings = await dbContext.EmailSettings.SingleAsync();
        Assert.Equal("new@example.org", settings.FromAddress);
        Assert.Equal("new-admin@example.org", settings.AdminNotificationEmail);
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_UnknownTeam_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var result = await CreateService(dbContext).UpdateEmailSettingsAsync(
            999, "a@example.org", null, "b@example.org", "https://example.org/privacy", "c@example.org", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }
}
