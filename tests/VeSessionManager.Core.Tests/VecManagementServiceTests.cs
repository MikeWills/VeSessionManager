using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class VecManagementServiceTests
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

    private static VecManagementService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<User> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task CreateAsync_NewName_CreatesVecAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", true, "Notes", user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.NotNull(vec);
        Assert.Equal("ARRL", vec!.Name);
        Assert.True(vec.SupportsYouthProgram);

        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("VecCreated", audit.Action);
        Assert.Equal(nameof(Vec), audit.EntityType);
        Assert.Equal(vec.Id, audit.EntityId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsDuplicateName_DoesNotCreate()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        await CreateService(dbContext).CreateAsync("ARRL", false, null, user.Id, CancellationToken.None);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.DuplicateName, result);
        Assert.Null(vec);
        Assert.Single(dbContext.Vecs);
    }

    [Fact]
    public async Task UpdateAsync_ExistingVec_UpdatesFieldsAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "ARRL", SupportsYouthProgram = false };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(vec.Id, "ARRL Updated", true, "New notes", user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        var updated = await dbContext.Vecs.SingleAsync();
        Assert.Equal("ARRL Updated", updated.Name);
        Assert.True(updated.SupportsYouthProgram);
        Assert.Equal("New notes", updated.Notes);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task UpdateAsync_UnknownVec_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var result = await CreateService(dbContext).UpdateAsync(999, "Name", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_RenamingToAnotherVecsName_ReturnsDuplicateName()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vecA = new Vec { Name = "ARRL" };
        var vecB = new Vec { Name = "W5YI" };
        dbContext.Vecs.AddRange(vecA, vecB);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(vecB.Id, "ARRL", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.DuplicateName, result);
    }
}
