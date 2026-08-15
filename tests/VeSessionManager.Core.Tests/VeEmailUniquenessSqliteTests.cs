using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// "One VE per email" is enforced by the database now, not only by four code paths that could all
/// pass concurrently (#284).
///
/// <para><b>Real SQLite, necessarily.</b> InMemory enforces neither unique indexes nor collations, so
/// it would report success whichever way this was written — which is how the rule survived this long
/// as four checks and no constraint.</para>
///
/// <para>The case-insensitivity is the part worth pinning. The four checks compare
/// <c>Email.ToLower() == …</c>, and a default SQLite index is case-<i>sensitive</i>: without NOCASE
/// the index would happily accept "A@x.com" beside "a@x.com" while the application refused them,
/// which is a guard that reads as settled and is not.</para>
/// </summary>
public class VeEmailUniquenessSqliteTests
{
    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static VolunteerExaminer Ve(string name, string? email) =>
        new() { Name = name, CallSign = null, Email = email, CreatedUtc = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public async Task TwoVEsCannotShareAnEmail()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.Add(Ve("First", "shared@example.com"));
        await dbContext.SaveChangesAsync();

        dbContext.VolunteerExaminers.Add(Ve("Second", "shared@example.com"));
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>The half a default index would have missed.</summary>
    [Fact]
    public async Task TwoVEsCannotShareAnEmailDifferingOnlyInCase()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.Add(Ve("First", "Person@Example.com"));
        await dbContext.SaveChangesAsync();

        dbContext.VolunteerExaminers.Add(Ve("Second", "person@example.com"));
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// Most VEs have no email at all — one of 176 did when self-service shipped — so the index has to
    /// tolerate any number of nulls. SQLite treats NULLs as distinct anyway; the filter states it.
    /// </summary>
    [Fact]
    public async Task ManyVEsCanHaveNoEmail()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        for (var i = 0; i < 5; i++)
        {
            dbContext.VolunteerExaminers.Add(Ve($"No email {i}", null));
        }

        await dbContext.SaveChangesAsync();
        Assert.Equal(5, await dbContext.VolunteerExaminers.CountAsync());
    }

    /// <summary>Different addresses are still fine — the index must not be over-eager.</summary>
    [Fact]
    public async Task DifferentEmailsAreUnaffected()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.Add(Ve("First", "one@example.com"));
        dbContext.VolunteerExaminers.Add(Ve("Second", "two@example.com"));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());
    }
}
