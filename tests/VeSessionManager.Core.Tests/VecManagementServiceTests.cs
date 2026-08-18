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
    public async Task CreateAsync_BlankCode_StoresNullAndMatchesOnTheName(string? code)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", code, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Null(vec!.ExamToolsCode);
        Assert.Equal("ARRL", vec.MatchCode);
    }

    /// <summary>
    /// <b>Reversed by #402.</b> A code equal to the name used to be discarded, "otherwise a later
    /// rename would strand the code on the old spelling" — but that spelling is what ExamTools sends,
    /// so stranding it there is right and discarding it is what let a rename break ingestion. Typing
    /// it now means it, and matching stays case-insensitive either way.
    /// </summary>
    [Theory]
    [InlineData("ARRL")]
    [InlineData("arrl")]
    public async Task CreateAsync_CodeMatchingTheName_IsStored(string code)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await CreateService(dbContext).CreateAsync("ARRL", code, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Equal(code, vec!.ExamToolsCode);
        Assert.Equal(code, vec.MatchCode);
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

    /// <summary>
    /// <b>Issue #402, and it cost five days of a team's sessions.</b> A VEC with no ExamTools code is
    /// matched on its <i>name</i>, so renaming it — an apparently cosmetic edit — silently re-points
    /// what ingestion matches. HRCC's ARRL was renamed on the beta box; every ARRL session created
    /// afterwards was skipped for want of a matching VEC, the job reported <c>Success</c> with the
    /// count buried in its summary, and the visible symptom was "sessions are missing from the app".
    ///
    /// <para>ExamTools' <c>vec</c> value is upstream data. Nothing done to a local display label can
    /// change it, so a rename must never change the match: the old name is frozen into the code, which
    /// is what the field was always implicitly holding.</para>
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RenamingAVecWithNoCode_FreezesTheOldNameAsTheCode()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(
            vec.Id, "ARRL VEC (Newington)", null, false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        var updated = await dbContext.Vecs.SingleAsync();
        Assert.Equal("ARRL VEC (Newington)", updated.Name);
        Assert.Equal("ARRL", updated.ExamToolsCode);
        // The whole point: what ingestion matches on did not move.
        Assert.Equal("ARRL", updated.MatchCode);
    }

    /// <summary>
    /// The escape hatch has to keep working: an admin renaming *because the old name was wrong* types
    /// the real code, and that wins over the freeze.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RenamingAndTypingACode_KeepsTheTypedCode()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "LA Group" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(
            vec.Id, "GLAARG", "lagroup", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Equal("lagroup", (await dbContext.Vecs.SingleAsync()).ExamToolsCode);
    }

    /// <summary>
    /// Clearing a code that was really set is a deliberate "match on the name" — the freeze applies
    /// only where the code was already implicit, so this is not overridden.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RenamingAndClearingARealCode_HonoursTheClear()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(
            vec.Id, "lagroup", "", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        var updated = await dbContext.Vecs.SingleAsync();
        Assert.Null(updated.ExamToolsCode);
        Assert.Equal("lagroup", updated.MatchCode);
    }

    /// <summary>
    /// A code typed to match the name is now <b>stored</b>, not discarded. It used to be nulled "so a
    /// later rename would not strand it on the old spelling" — which is precisely the behaviour that
    /// broke #402: stranding it on the old spelling is correct, because that spelling is what
    /// ExamTools sends.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ACodeMatchingTheName_IsStoredRatherThanDiscarded()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).UpdateAsync(
            vec.Id, "ARRL", "ARRL", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.Success, result);
        Assert.Equal("ARRL", (await dbContext.Vecs.SingleAsync()).ExamToolsCode);
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
