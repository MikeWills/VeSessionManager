using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SystemSettingsServiceTests
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

    private static SystemSettingsService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    [Fact]
    public async Task GetAsync_NoRowExists_CreatesDefaultSingletonRow()
    {
        await using var dbContext = CreateContext();

        var settings = await CreateService(dbContext).GetAsync(CancellationToken.None);

        Assert.Equal(SystemSettingsService.SingletonId, settings.Id);
        Assert.Null(settings.PiiRetentionWindowDays);
        Assert.Equal(12, settings.UlsWatcherIntervalHours);
        Assert.Equal(8, settings.UlsWatcherStartHourEt);
        Assert.Equal(60, settings.SessionIngestionIntervalMinutes);
    }

    [Fact]
    public async Task GetAsync_RowAlreadyExists_ReturnsExistingRow_DoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            PiiRetentionWindowDays = 90,
            UlsWatcherIntervalHours = 12,
            UlsWatcherStartHourEt = 8
        });
        await dbContext.SaveChangesAsync();

        var settings = await CreateService(dbContext).GetAsync(CancellationToken.None);

        Assert.Equal(90, settings.PiiRetentionWindowDays);
        Assert.Single(dbContext.SystemSettings);
    }

    [Fact]
    public async Task UpdateAsync_ValidValues_UpdatesRowAndWritesAuditEntry()
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(90, veContactRetentionYears: null, auditLogRetentionDays: null, jobRunHistoryRetentionDays: null, vecSubmissionArchiveRetentionDays: null, 12, 9, 15, testModeEnabled: false, testModeOverrideEmail: null, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.Success, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Equal(90, settings.PiiRetentionWindowDays);
        Assert.Equal(12, settings.UlsWatcherIntervalHours);
        Assert.Equal(9, settings.UlsWatcherStartHourEt);
        Assert.Equal(15, settings.SessionIngestionIntervalMinutes);
        Assert.Equal(user.Id, settings.UpdatedByUserId);
        Assert.Equal(Now, settings.UpdatedUtc);

        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("SystemSettingsUpdated", audit.Action);
        Assert.Equal(nameof(SystemSettings), audit.EntityType);
        Assert.Equal(SystemSettingsService.SingletonId, audit.EntityId);
        Assert.Equal(user.Id, audit.UserId);
    }

    [Fact]
    public async Task UpdateAsync_NullRetentionWindow_IsAllowed_MeansNotYetSet()
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(null, veContactRetentionYears: null, auditLogRetentionDays: null, jobRunHistoryRetentionDays: null, vecSubmissionArchiveRetentionDays: null, 24, 8, 60, testModeEnabled: false, testModeOverrideEmail: null, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.Success, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Null(settings.PiiRetentionWindowDays);
    }

    [Theory]
    [InlineData(0, 8, 24, 60)]     // interval < 1
    [InlineData(24, 8, 0, 60)]     // retention < 1
    [InlineData(24, 8, 24, 0)]     // session ingestion < 1
    [InlineData(24, -1, 24, 60)]   // start hour below range
    [InlineData(24, 24, 24, 60)]   // start hour above range
    public async Task UpdateAsync_InvalidIntervalOrRetentionOrStartHour_ReturnsInvalidValue_ChangesNothing(
        int intervalHours, int startHourEt, int retention, int sessionIngestionMinutes)
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var original = await CreateService(dbContext).GetAsync(CancellationToken.None);
        var originalInterval = original.UlsWatcherIntervalHours;

        var result = await CreateService(dbContext).UpdateAsync(retention, veContactRetentionYears: null, auditLogRetentionDays: null, jobRunHistoryRetentionDays: null, vecSubmissionArchiveRetentionDays: null, intervalHours, startHourEt, sessionIngestionMinutes, testModeEnabled: false, testModeOverrideEmail: null, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.InvalidValue, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Equal(originalInterval, settings.UlsWatcherIntervalHours);
        Assert.Empty(dbContext.AuditLogs);
    }

    [Fact]
    public async Task UpdateAsync_TestModeEnabled_WithOverrideEmail_Succeeds()
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(90, veContactRetentionYears: null, auditLogRetentionDays: null, jobRunHistoryRetentionDays: null, vecSubmissionArchiveRetentionDays: null, 24, 8, 60, testModeEnabled: true, testModeOverrideEmail: "tester@example.com", user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.Success, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.True(settings.TestModeEnabled);
        Assert.Equal("tester@example.com", settings.TestModeOverrideEmail);
    }

    [Fact]
    public async Task UpdateAsync_TestModeEnabled_WithoutOverrideEmail_ReturnsTestModeMissingOverrideEmail_ChangesNothing()
    {
        // Turning test mode on with no override address would silently drop every email instead of
        // redirecting it, so this must be rejected before anything is saved.
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        await CreateService(dbContext).GetAsync(CancellationToken.None); // ensure the singleton row already exists

        var result = await CreateService(dbContext).UpdateAsync(90, veContactRetentionYears: null, auditLogRetentionDays: null, jobRunHistoryRetentionDays: null, vecSubmissionArchiveRetentionDays: null, 24, 8, 60, testModeEnabled: true, testModeOverrideEmail: "  ", user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.TestModeMissingOverrideEmail, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.False(settings.TestModeEnabled);
        Assert.Empty(dbContext.AuditLogs);
    }
}
