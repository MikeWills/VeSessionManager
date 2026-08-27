using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;
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
        new(dbContext, new FixedTimeProvider(Now), NullLogger<TeamSettingsService>.Instance);

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

    /// <summary>
    /// Reported from the live beta (2026-08-04): a team created here had no EmailSettings row and no
    /// messages until the Worker next started, because EmailDefaultsSeeder only ran at Worker
    /// startup over the teams that existed then. The visible symptom was nothing seeded for this
    /// team yet; the invisible one was CandidateNotificationService skipping the team entirely and
    /// sending nothing.
    /// </summary>
    [Fact]
    public async Task CreateAsync_SeedsEmailSettingsAndMessageRules_WithoutNeedingAWorkerRestart()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (_, team) = await CreateService(dbContext).CreateAsync("New Team", user.Id, CancellationToken.None);

        Assert.NotNull(team);
        Assert.True(await dbContext.EmailSettings.AnyAsync(e => e.TeamId == team!.Id));
        var triggers = await dbContext.MessageRules.Where(r => r.TeamId == team!.Id).Select(r => r.Trigger).ToListAsync();
        Assert.Contains(MessageTrigger.CandidateRegistered, triggers);
        Assert.Contains(MessageTrigger.BeforeSessionStart, triggers);
    }

    /// <summary>The Worker's startup sweep still runs over every team, so seeding must not duplicate what CreateAsync already wrote.</summary>
    [Fact]
    public async Task CreateAsync_ThenTheWorkersStartupSweep_DoesNotDuplicateTheSeededRows()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var (_, team) = await CreateService(dbContext).CreateAsync("New Team", user.Id, CancellationToken.None);
        var ruleCountAfterCreate = await dbContext.MessageRules.CountAsync(r => r.TeamId == team!.Id);

        await EmailDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(1, await dbContext.EmailSettings.CountAsync(e => e.TeamId == team!.Id));
        Assert.Equal(ruleCountAfterCreate, await dbContext.MessageRules.CountAsync(r => r.TeamId == team!.Id));
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

        var result = await CreateService(dbContext).UpdateExamToolsAsync(team.Id, "WX0MIK", "admin", "secret-password", "https://examtools.dev", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("WX0MIK", updated.ExamToolsTeamCode);
        Assert.Equal("admin", updated.ExamToolsUsername);
        Assert.Equal("secret-password", updated.ExamToolsPassword);
        Assert.Equal("https://examtools.dev", updated.ExamToolsBaseUrl);
    }

    [Fact]
    public async Task UpdateExamToolsAsync_BlankBaseUrl_ClearsOverrideBackToNull()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.ExamToolsBaseUrl = "https://examtools.dev";
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateExamToolsAsync(team.Id, "WX0MIK", "admin", null, "  ", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Null(updated.ExamToolsBaseUrl);
    }

    [Fact]
    public async Task UpdateExamToolsAsync_NullPassword_LeavesExistingPasswordUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.ExamToolsPassword = "original-secret";
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateExamToolsAsync(team.Id, "WX0MIK", "admin", null, null, user.Id, CancellationToken.None);

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

        var result = await CreateService(dbContext).UpdateExamToolsAsync(999, "WX0MIK", "admin", "secret", null, user.Id, CancellationToken.None);

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

        await CreateService(dbContext).UpdateZoomAsync(team.Id, "acct", "client-id", null, "me", 3, user.Id, CancellationToken.None);

        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-zoom-secret", updated.ZoomClientSecret);
        Assert.Equal("client-id", updated.ZoomClientId);
        Assert.Equal(3, updated.ZoomBreakoutRoomCount);
    }

    [Fact]
    public async Task UpdateDiscordAsync_SetsGuildId_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateDiscordAsync(team.Id, 1323140214008578111UL, null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal(1323140214008578111UL, updated.DiscordGuildId);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("TeamDiscordSettingsUpdated", audit.Action);
    }

    [Fact]
    public async Task UpdatePurgeSettingsAsync_SetsDays_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdatePurgeSettingsAsync(team.Id, 45, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal(45, updated.PurgeUnpaidLinkDays);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("TeamPurgeSettingsUpdated", audit.Action);
    }

    [Fact]
    public async Task UpdatePurgeSettingsAsync_UnknownTeam_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var result = await CreateService(dbContext).UpdatePurgeSettingsAsync(999, 45, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }

    [Fact]
    public async Task NewTeam_DefaultsPurgeUnpaidLinkDaysTo30()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        Assert.Equal(30, team.PurgeUnpaidLinkDays);
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

        await CreateService(dbContext).UpdateSquareAsync(team.Id, null, "loc-1", null, "https://host/webhooks/square/1", SquareApiEnvironment.Sandbox, user.Id, CancellationToken.None);

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

        await CreateService(dbContext).UpdateSmtpAsync(team.Id, "smtp.mailgun.org", 587, "postmaster@example.org", null, user.Id, CancellationToken.None);

        var updated = await dbContext.Teams.SingleAsync();
        Assert.Equal("original-smtp-secret", updated.SmtpPassword);
        Assert.Equal("smtp.mailgun.org", updated.SmtpHost);
        Assert.Equal(587, updated.SmtpPort);
        // SmtpUseStartTls is no longer written or read (issue #259) — TLS is mandatory and chosen by
        // the port, so the column is vestigial. Asserted null rather than deleted, because "the save
        // path stopped touching it" is the fact worth pinning while the column still exists.
        Assert.Null(updated.SmtpUseStartTls);
    }

    /// <summary>
    /// The watermark <c>MessageRuleEligibility.FloorUtc</c> reads for every email-channel rule
    /// (2026-08-25) — stamped only on the actual off-to-on transition, never on a team that was
    /// already configured, so an already-running team's history is never retroactively bounded.
    /// </summary>
    [Fact]
    public async Task UpdateSmtpAsync_FirstTimeConfigured_StampsEmailConfiguredUtc()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        Assert.Null(team.EmailConfiguredUtc);

        await CreateService(dbContext).UpdateSmtpAsync(team.Id, "smtp.mailgun.org", 587, "postmaster@example.org", "secret", user.Id, CancellationToken.None);

        Assert.Equal(Now, (await dbContext.Teams.SingleAsync()).EmailConfiguredUtc);
    }

    /// <summary>A team already configured has had no off-to-on transition — updating its credentials again must not move the watermark.</summary>
    [Fact]
    public async Task UpdateSmtpAsync_AlreadyConfigured_DoesNotRestampEmailConfiguredUtc()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.SmtpHost = "smtp.mailgun.org";
        team.SmtpUsername = "postmaster@example.org";
        team.EmailConfiguredUtc = Now.AddDays(-30);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateSmtpAsync(team.Id, "smtp.mailgun.org", 587, "postmaster@example.org", "new-secret", user.Id, CancellationToken.None);

        Assert.Equal(Now.AddDays(-30), (await dbContext.Teams.SingleAsync()).EmailConfiguredUtc);
    }

    /// <summary>Clearing credentials back to unconfigured, then re-entering them, is itself an off-to-on transition and re-stamps.</summary>
    [Fact]
    public async Task UpdateSmtpAsync_ReconfiguredAfterBeingCleared_RestampsEmailConfiguredUtc()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.EmailConfiguredUtc = Now.AddDays(-30);
        await dbContext.SaveChangesAsync();
        // Cleared back to unconfigured.
        await CreateService(dbContext).UpdateSmtpAsync(team.Id, null, null, null, null, user.Id, CancellationToken.None);
        Assert.False((await dbContext.Teams.SingleAsync()).IsEmailConfigured);

        await CreateService(dbContext).UpdateSmtpAsync(team.Id, "smtp.mailgun.org", 587, "postmaster@example.org", "secret", user.Id, CancellationToken.None);

        Assert.Equal(Now, (await dbContext.Teams.SingleAsync()).EmailConfiguredUtc);
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_NoExistingRow_CreatesOne()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateEmailSettingsAsync(
            team.Id, "noreply@example.org", "VE Team", "reply@example.org", "https://example.org/privacy", "admin@example.org", null, user.Id, CancellationToken.None);

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
            team.Id, "new@example.org", null, "new-reply@example.org", "https://example.org/privacy", "new-admin@example.org", null, user.Id, CancellationToken.None);

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
            999, "a@example.org", null, "b@example.org", "https://example.org/privacy", "c@example.org", null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }


    [Theory]
    [InlineData("watch@example.org", "watch@example.org")]
    [InlineData("  watch@example.org  ", "watch@example.org")]   // trimmed
    [InlineData("", null)]                                       // blank means off, stored as null
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task UpdateEmailSettingsAsync_NormalizesTheBccAddress(string? entered, string? expected)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        await CreateService(dbContext).UpdateEmailSettingsAsync(
            team.Id, "noreply@example.org", null, "reply@example.org", "https://example.org/privacy",
            "admin@example.org", entered, user.Id, CancellationToken.None);

        Assert.Equal(expected, (await dbContext.EmailSettings.SingleAsync()).BccAddress);
    }

    /// <summary>
    /// Turning candidate-mail monitoring on or off starts copying personal data somewhere new, so
    /// the audit entry says which it is. The address itself is deliberately not recorded — it is an
    /// admin's own inbox, and no other field on this row logs a raw address either.
    /// </summary>
    [Fact]
    public async Task UpdateEmailSettingsAsync_AuditRecordsWhetherTheBccIsOn()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);

        await service.UpdateEmailSettingsAsync(team.Id, "a@example.org", null, "b@example.org",
            "https://example.org/privacy", "c@example.org", "watch@example.org", user.Id, CancellationToken.None);
        await service.UpdateEmailSettingsAsync(team.Id, "a@example.org", null, "b@example.org",
            "https://example.org/privacy", "c@example.org", null, user.Id, CancellationToken.None);

        var entries = await dbContext.AuditLogs.Where(a => a.Action == "TeamEmailSettingsUpdated").ToListAsync();
        Assert.Contains(entries, e => e.Details?.Contains("BCC on") == true);
        Assert.Contains(entries, e => e.Details?.Contains("BCC off") == true);
        Assert.DoesNotContain(entries, e => e.Details?.Contains("watch@example.org") == true);
    }

    [Fact]
    public async Task UpdateYouthConfirmIntroAsync_UpdatesOnlyTheIntroText_LeavesRestOfEmailSettingsUntouched()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateYouthConfirmIntroAsync(team.Id, "<p>Custom intro</p>", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        var settings = await dbContext.EmailSettings.SingleAsync();
        Assert.Equal("<p>Custom intro</p>", settings.YouthConfirmIntroHtml);
        Assert.Equal("noreply@example.org", settings.FromAddress);
        Assert.Equal(user.Id, settings.UpdatedByUserId);
        Assert.Equal(Now, settings.UpdatedUtc);
    }

    [Fact]
    public async Task UpdateYouthConfirmIntroAsync_BlankText_StoresNull()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org",
            YouthConfirmIntroHtml = "<p>Old text</p>"
        });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).UpdateYouthConfirmIntroAsync(team.Id, "   ", user.Id, CancellationToken.None);

        Assert.Null((await dbContext.EmailSettings.SingleAsync()).YouthConfirmIntroHtml);
    }

    [Fact]
    public async Task UpdateYouthConfirmIntroAsync_NoExistingEmailSettingsRow_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var result = await CreateService(dbContext).UpdateYouthConfirmIntroAsync(team.Id, "<p>Text</p>", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }

    // ---- Square environment (2026-08-06) ---------------------------------------------------------
    // Moved off a global SquareOptions setting because a token is issued FOR an environment and only
    // authenticates against that host — so one global switch made "real team on Production, test team
    // on Sandbox" impossible.

    [Fact]
    public async Task NewTeam_DefaultsToSandbox_SoItCannotTakeRealMoneyUnasked()
    {
        await using var dbContext = CreateContext();
        await SeedTeamAsync(dbContext, "FRESH");

        Assert.Equal(SquareApiEnvironment.Sandbox, (await dbContext.Teams.SingleAsync()).SquareEnvironment);
    }

    [Theory]
    [InlineData(SquareApiEnvironment.Production)]
    [InlineData(SquareApiEnvironment.Sandbox)]
    public async Task UpdateSquare_StoresTheChosenEnvironment(SquareApiEnvironment environment)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        await CreateService(dbContext).UpdateSquareAsync(team.Id, "tok", "loc", "key", "https://host/webhooks/square/1", environment, user.Id, CancellationToken.None);

        Assert.Equal(environment, (await dbContext.Teams.SingleAsync()).SquareEnvironment);
    }

    /// <summary>Two teams on one deployment can differ — the case a single global setting made impossible.</summary>
    [Fact]
    public async Task TwoTeams_CanUseDifferentEnvironments()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var live = await SeedTeamAsync(dbContext, "LIVE");
        var test = await SeedTeamAsync(dbContext, "TESTTEAM");

        var service = CreateService(dbContext);
        await service.UpdateSquareAsync(live.Id, "tok", "loc", "key", "https://host/webhooks/square/1", SquareApiEnvironment.Production, user.Id, CancellationToken.None);
        await service.UpdateSquareAsync(test.Id, "tok", "loc", "key", "https://host/webhooks/square/2", SquareApiEnvironment.Sandbox, user.Id, CancellationToken.None);

        Assert.Equal(SquareApiEnvironment.Production, (await dbContext.Teams.FindAsync(live.Id))!.SquareEnvironment);
        Assert.Equal(SquareApiEnvironment.Sandbox, (await dbContext.Teams.FindAsync(test.Id))!.SquareEnvironment);
    }

    /// <summary>The credentials handed to SquareClient carry the environment, so the client cannot use a different one.</summary>
    [Fact]
    public async Task ToSquareCredentials_CarriesTheTeamsEnvironment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.SquareAccessToken = "tok";
        team.SquareLocationId = "loc";
        team.SquareEnvironment = SquareApiEnvironment.Production;

        var credentials = team.ToSquareCredentials();

        Assert.Equal(SquareApiEnvironment.Production, credentials.Environment);
        Assert.Equal("tok", credentials.AccessToken);
        Assert.Equal("loc", credentials.LocationId);
    }
}
