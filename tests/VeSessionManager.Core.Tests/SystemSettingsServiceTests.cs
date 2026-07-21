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
        Assert.Equal(24, settings.FccDailyWatcherIntervalHours);
        Assert.Equal(24, settings.FccWeeklyCatchupIntervalHours);
        Assert.Equal(DayOfWeek.Monday, settings.FccWeeklyCatchupDayOfWeek);
    }

    [Fact]
    public async Task GetAsync_RowAlreadyExists_ReturnsExistingRow_DoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            PiiRetentionWindowDays = 90,
            FccDailyWatcherIntervalHours = 12,
            FccWeeklyCatchupIntervalHours = 12,
            FccWeeklyCatchupDayOfWeek = DayOfWeek.Sunday
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

        var result = await CreateService(dbContext).UpdateAsync(90, 12, 48, DayOfWeek.Sunday, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.Success, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Equal(90, settings.PiiRetentionWindowDays);
        Assert.Equal(12, settings.FccDailyWatcherIntervalHours);
        Assert.Equal(48, settings.FccWeeklyCatchupIntervalHours);
        Assert.Equal(DayOfWeek.Sunday, settings.FccWeeklyCatchupDayOfWeek);
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

        var result = await CreateService(dbContext).UpdateAsync(null, 24, 24, DayOfWeek.Monday, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.Success, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Null(settings.PiiRetentionWindowDays);
    }

    [Theory]
    [InlineData(0, 24, 24)]
    [InlineData(24, 0, 24)]
    [InlineData(24, 24, 0)]
    public async Task UpdateAsync_InvalidIntervalOrRetention_ReturnsInvalidValue_ChangesNothing(int daily, int weekly, int retention)
    {
        await using var dbContext = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var original = await CreateService(dbContext).GetAsync(CancellationToken.None);
        var originalDaily = original.FccDailyWatcherIntervalHours;

        var result = await CreateService(dbContext).UpdateAsync(retention, daily, weekly, DayOfWeek.Monday, user.Id, CancellationToken.None);

        Assert.Equal(SystemSettingsActionResult.InvalidValue, result);
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.Equal(originalDaily, settings.FccDailyWatcherIntervalHours);
        Assert.Empty(dbContext.AuditLogs);
    }
}
