using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Run against real SQLite rather than the InMemory provider the rest of the suite uses: the whole
/// point of the seeder's skip logic is to avoid tripping <c>IX_Vecs_Name</c> and
/// <c>IX_Vecs_ExamToolsCode</c>, and a unique index is provider behaviour InMemory does not enforce.
/// A test that passed on InMemory would prove nothing about the case that actually throws.
/// </summary>
public class VecDefaultsSeederTests
{
    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(); // an in-memory DB lives only as long as its connection
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    [Fact]
    public async Task EmptyDatabase_SeedsEveryKnownVec_WithCodeNulledOnlyWhenItEqualsTheName()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var seeded = await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(KnownVecs.All.Count, seeded);
        var rows = await dbContext.Vecs.ToListAsync();
        Assert.Equal(KnownVecs.All.Count, rows.Count);

        // MatchCode is what ingestion resolves against, so every known code must be reachable.
        Assert.Equal(
            KnownVecs.All.Select(v => v.Code).OrderBy(c => c),
            rows.Select(r => r.MatchCode.ToLowerInvariant()).OrderBy(c => c));

        // W5YI's code equals its name, so it follows NormalizeCode's rule and stores null.
        Assert.Null(rows.Single(r => r.Name == "W5YI").ExamToolsCode);
        // GLAARG's does not, and is the case the whole ExamToolsCode column exists for.
        Assert.Equal("lagroup", rows.Single(r => r.Name == "GLAARG").ExamToolsCode);
    }

    [Fact]
    public async Task RunningTwice_AddsNothingTheSecondTime()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);
        var secondRun = await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(0, secondRun);
        Assert.Equal(KnownVecs.All.Count, await dbContext.Vecs.CountAsync());
    }

    /// <summary>
    /// The case this deployment is actually in: rows created by hand under different names, some
    /// with the code already set. None of them may be renamed, re-coded or duplicated.
    /// </summary>
    [Fact]
    public async Task ExistingRows_AreLeftExactlyAsTheyWere_AndAreNotDuplicated()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Vecs.AddRange(
            new Vec { Name = "ARRL", SupportsYouthProgram = true, Notes = "Hand-created" },
            new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" },
            new Vec { Name = "SANDARC" });
        await dbContext.SaveChangesAsync();

        var seeded = await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(KnownVecs.All.Count - 3, seeded);
        Assert.Equal(KnownVecs.All.Count, await dbContext.Vecs.CountAsync());

        // "ARRL" is kept, not renamed to HamStudy's "ARRL-VEC", and no second arrl-coded row exists.
        var arrl = Assert.Single(await dbContext.Vecs.Where(v => v.Name == "ARRL").ToListAsync());
        Assert.Null(arrl.ExamToolsCode);
        Assert.Equal("Hand-created", arrl.Notes);
        Assert.False(await dbContext.Vecs.AnyAsync(v => v.Name == "ARRL-VEC"));
    }

    /// <summary>
    /// A row named exactly like a known VEC but resolving to some other code cannot be inserted
    /// alongside — <c>IX_Vecs_Name</c> is unique. The seeder has to skip it rather than throw and
    /// take down Worker startup with it.
    /// </summary>
    [Fact]
    public async Task NameTakenByARowWithADifferentCode_SkipsInsteadOfViolatingTheUniqueIndex()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Vecs.Add(new Vec { Name = "GLAARG", ExamToolsCode = "typo-not-lagroup" });
        await dbContext.SaveChangesAsync();

        var seeded = await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(KnownVecs.All.Count - 1, seeded);
        var glaarg = Assert.Single(await dbContext.Vecs.Where(v => v.Name == "GLAARG").ToListAsync());
        Assert.Equal("typo-not-lagroup", glaarg.ExamToolsCode);
    }

    [Fact]
    public async Task MatchIsCaseInsensitive_SoALowercaseHandTypedRowIsNotDuplicated()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Vecs.Add(new Vec { Name = "w5yi" });
        await dbContext.SaveChangesAsync();

        await VecDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(KnownVecs.All.Count, await dbContext.Vecs.CountAsync());
        Assert.Single(await dbContext.Vecs.Where(v => v.Name == "w5yi").ToListAsync());
    }

    [Fact]
    public void KnownVecs_HaveNoDuplicateCodesOrNames()
    {
        Assert.Equal(KnownVecs.All.Count, KnownVecs.All.Select(v => v.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(KnownVecs.All.Count, KnownVecs.All.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(KnownVecs.All, v => Assert.Equal(v.Code, v.Code.ToLowerInvariant()));
    }
}
