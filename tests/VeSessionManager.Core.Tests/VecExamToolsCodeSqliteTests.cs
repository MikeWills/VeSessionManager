using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The rest of the suite runs on the EF InMemory provider, which evaluates predicates as plain LINQ
/// and so cannot tell whether a query actually translates to SQL. Vec matching is a case where that
/// gap matters: <see cref="Ingestion.SessionIngestionService"/> resolves a session's VEC with a
/// <c>(ExamToolsCode ?? Name)</c> coalesce, and the ExamToolsCode unique index relies on SQLite
/// treating NULLs as distinct. Both are provider behavior, so both are pinned here against real
/// SQLite — the same provider production runs on.
/// </summary>
public class VecExamToolsCodeSqliteTests
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
    public async Task CoalescedMatchCode_TranslatesToSql_AndMatchesEitherColumn()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Vecs.AddRange(
            new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" },
            new Vec { Name = "ARRL" });
        await dbContext.SaveChangesAsync();

        // Exactly the shape SessionIngestionService.TryCreateSessionAsync uses. If this ever stops
        // translating, EF throws here rather than silently falling back to client evaluation.
        async Task<Vec?> MatchAsync(string vecCode) =>
            await dbContext.Vecs.FirstOrDefaultAsync(v => (v.ExamToolsCode ?? v.Name).ToLower() == vecCode);

        Assert.Equal("GLAARG", (await MatchAsync("lagroup"))?.Name);
        Assert.Equal("ARRL", (await MatchAsync("arrl"))?.Name);
        Assert.Null(await MatchAsync("glaarg"));  // the name is not the code once a code is set
        Assert.Null(await MatchAsync("w5yi"));
    }

    [Fact]
    public async Task ExamToolsCodeUniqueIndex_AllowsManyNulls_ButRejectsADuplicateCode()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        // Most VECs leave the code null (it equals the name); the unique index must not treat those
        // as colliding with each other, or adding a second ordinary VEC would fail outright.
        dbContext.Vecs.AddRange(
            new Vec { Name = "ARRL" },
            new Vec { Name = "W5YI" },
            new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" });
        await dbContext.SaveChangesAsync();
        Assert.Equal(3, await dbContext.Vecs.CountAsync());

        dbContext.Vecs.Add(new Vec { Name = "Another", ExamToolsCode = "lagroup" });
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }
}
