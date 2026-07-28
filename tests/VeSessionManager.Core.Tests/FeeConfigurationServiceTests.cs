using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class FeeConfigurationServiceTests
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

    private static FeeConfigurationService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<User> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<Vec> SeedVecAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        return vec;
    }

    private static async Task<FeeConfiguration> SeedFeeConfigurationAsync(AppDbContext dbContext, Vec vec, User user)
    {
        var feeConfiguration = new FeeConfiguration
        {
            VecId = vec.Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            RetainedAmount = 5m,
            CreatedByUserId = user.Id,
            CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        return feeConfiguration;
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TEAMA", CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, Vec vec, FeeConfiguration feeConfiguration)
    {
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Test Session",
            ScheduledStartUtc = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task CreateAsync_ValidVec_CreatesFeeConfigurationAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);

        var (result, feeConfiguration) = await CreateService(dbContext).CreateAsync(
            vec.Id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), true, 15m, 5m, null, "Notes", user.Id, CancellationToken.None);

        Assert.Equal(FeeConfigActionResult.Success, result);
        Assert.NotNull(feeConfiguration);
        Assert.Equal(15m, feeConfiguration!.ExamFeeAmount);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("FeeConfigurationCreated", audit.Action);
        Assert.Equal(nameof(FeeConfiguration), audit.EntityType);
        Assert.Equal(feeConfiguration.Id, audit.EntityId);
    }

    [Fact]
    public async Task CreateAsync_UnknownVec_ReturnsVecNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, feeConfiguration) = await CreateService(dbContext).CreateAsync(
            999, Now, true, 15m, 5m, null, null, user.Id, CancellationToken.None);

        Assert.Equal(FeeConfigActionResult.VecNotFound, result);
        Assert.Null(feeConfiguration);
    }

    [Fact]
    public async Task CreateAsync_FeeCollectionDisabled_NullsOutAmounts()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);

        var (_, feeConfiguration) = await CreateService(dbContext).CreateAsync(
            vec.Id, Now, false, 15m, 5m, null, null, user.Id, CancellationToken.None);

        Assert.Null(feeConfiguration!.ExamFeeAmount);
        Assert.Null(feeConfiguration.RetainedAmount);
    }

    [Fact]
    public async Task UpdateAsync_UnreferencedFeeConfiguration_Succeeds()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec, user);

        var result = await CreateService(dbContext).UpdateAsync(
            feeConfiguration.Id, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), true, 20m, 6m, null, "Updated", user.Id, CancellationToken.None);

        Assert.Equal(FeeConfigActionResult.Success, result);
        var updated = await dbContext.FeeConfigurations.SingleAsync();
        Assert.Equal(20m, updated.ExamFeeAmount);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task UpdateAsync_FeeConfigurationReferencedBySession_ReturnsInUse_LeavesSessionUnchanged()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec, user);
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration);

        var result = await CreateService(dbContext).UpdateAsync(
            feeConfiguration.Id, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), false, null, null, null, "Should not apply", user.Id, CancellationToken.None);

        Assert.Equal(FeeConfigActionResult.InUse, result);

        var unchangedFeeConfig = await dbContext.FeeConfigurations.SingleAsync();
        Assert.Equal(15m, unchangedFeeConfig.ExamFeeAmount);
        Assert.Equal(5m, unchangedFeeConfig.RetainedAmount);
        Assert.True(unchangedFeeConfig.FeeCollectionEnabled);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), unchangedFeeConfig.EffectiveDate);

        var reQueriedSession = await dbContext.Sessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(feeConfiguration.Id, reQueriedSession.FeeConfigurationId);
        Assert.Empty(dbContext.AuditLogs);
    }

    [Fact]
    public async Task UpdateAsync_UnknownFeeConfiguration_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var result = await CreateService(dbContext).UpdateAsync(999, Now, true, 15m, 5m, null, null, user.Id, CancellationToken.None);

        Assert.Equal(FeeConfigActionResult.NotFound, result);
    }
}
