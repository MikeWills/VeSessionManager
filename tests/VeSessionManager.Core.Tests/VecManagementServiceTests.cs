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

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", null, true, "Notes", user.Id, CancellationToken.None);

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
        await CreateService(dbContext).CreateAsync("ARRL", null, false, null, user.Id, CancellationToken.None);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", null, false, null, user.Id, CancellationToken.None);

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

        var result = await CreateService(dbContext).UpdateAsync(vec.Id, "ARRL Updated", null, true, "New notes", user.Id, CancellationToken.None);

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

        var result = await CreateService(dbContext).UpdateAsync(999, "Name", null, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.NotFound, result);
    }

    [Fact]
    public async Task CreateAsync_ExamToolsCodeDifferentFromName_IsStored()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await CreateService(dbContext).CreateAsync("GLAARG", "lagroup", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Equal("GLAARG", vec!.Name);
        Assert.Equal("lagroup", vec.ExamToolsCode);
        Assert.Equal("lagroup", vec.MatchCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ARRL")]   // same as the name — storing it would strand the code on a later rename
    [InlineData("arrl")]   // matching is case-insensitive, so this is still "same as the name"
    public async Task CreateAsync_BlankOrNameMatchingCode_StoresNull(string? code)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", code, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Null(vec!.ExamToolsCode);
        Assert.Equal("ARRL", vec.MatchCode);
    }

    [Fact]
    public async Task CreateAsync_CodeCollidingWithAnotherVecsName_ReturnsDuplicateExamToolsCode()
    {
        // The collision is against the *effective* match code, not just the ExamToolsCode column —
        // an existing VEC named "lagroup" (with a null code) occupies that code just as firmly.
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        dbContext.Vecs.Add(new Vec { Name = "lagroup" });
        await dbContext.SaveChangesAsync();

        var (result, vec) = await CreateService(dbContext).CreateAsync("GLAARG", "LAGROUP", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.DuplicateExamToolsCode, result);
        Assert.Null(vec);
    }

    [Fact]
    public async Task CreateAsync_FirstVecWithACode_DoesNotCollideWithItself()
    {
        // Regression guard for the create path passing excludingVecId: 0 — a nullable id would make
        // the SQL "Id <> NULL" and wave every duplicate through instead.
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, _) = await CreateService(dbContext).CreateAsync("GLAARG", "lagroup", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
    }

    [Fact]
    public async Task UpdateAsync_KeepingItsOwnCode_IsNotAnUpdateConflict()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(vec.Id, "GLAARG", "lagroup", true, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Equal("lagroup", (await dbContext.Vecs.SingleAsync()).ExamToolsCode);
    }

    [Fact]
    public async Task UpdateAsync_ClearingTheCode_FallsBackToTheName()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(vec.Id, "GLAARG", "", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        var updated = await dbContext.Vecs.SingleAsync();
        Assert.Null(updated.ExamToolsCode);
        Assert.Equal("GLAARG", updated.MatchCode);
    }

    [Fact]
    public async Task UpdateAsync_TakingAnotherVecsCode_ReturnsDuplicateExamToolsCode()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var glaarg = new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" };
        var w5yi = new Vec { Name = "W5YI" };
        dbContext.Vecs.AddRange(glaarg, w5yi);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(w5yi.Id, "W5YI", "lagroup", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.DuplicateExamToolsCode, result);
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

        var result = await CreateService(dbContext).UpdateAsync(vecB.Id, "ARRL", null, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.DuplicateName, result);
    }
}
