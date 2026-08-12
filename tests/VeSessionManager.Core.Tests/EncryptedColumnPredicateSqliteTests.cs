using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #279: <c>t.ExamToolsPassword != ""</c> against a column carrying an encrypting value
/// converter.
///
/// <para><b>Why real SQLite is the only way to see this.</b> EF translates the <c>""</c> constant
/// through the converter too, emitting a comparison against a freshly <c>Protect("")</c>'d
/// ciphertext — non-deterministic, so it can never equal any stored value, and the predicate is
/// always true. On the InMemory provider the same expression is evaluated as plain LINQ over
/// decrypted values, where it behaves exactly as written. <b>A test of this on InMemory passes
/// against the bug.</b></para>
///
/// <para>The consequence was a dashboard that disagreed with the job: an admin who cleared a team's
/// ExamTools password stored <c>Protect("")</c>, the Ingestion Status page and the site-wide health
/// banner reported the team as configured and due, and <c>SessionIngestionJob</c> correctly skipped
/// it via <c>Team.IsExamToolsConfigured</c>, which uses <c>IsNullOrWhiteSpace</c>. Nothing logged.</para>
///
/// <para>Fixed on both sides: the query keeps only the <c>!= null</c> half (EF special-cases null and
/// does not convert it), and the write path stores null rather than <c>""</c> so that half means what
/// it says.</para>
/// </summary>
public class EncryptedColumnPredicateSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static IngestionStatusService CreateService(AppDbContext dbContext) =>
        new(dbContext, new IngestionScheduleService(), new FixedTimeProvider(Now));

    private static Team NewTeam(string name, string? password) => new()
    {
        Name = name,
        ExamToolsTeamCode = name,
        ExamToolsUsername = "ve@example.com",
        ExamToolsPassword = password,
        CreatedUtc = Now
    };

    /// <summary>
    /// The stored form an admin's "clear the password" produced before the write-path half of the
    /// fix: a real row that still exists in any database that has been running.
    /// </summary>
    [Fact]
    public async Task ATeamWhoseStoredPasswordIsEmpty_IsReportedAsNotConfigured()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Teams.Add(NewTeam("BLANKPW", ""));
        dbContext.Teams.Add(NewTeam("REALPW", "secret"));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        var blank = Assert.Single(report.Teams, r => r.TeamName == "BLANKPW");
        var real = Assert.Single(report.Teams, r => r.TeamName == "REALPW");

        // The discriminating pair. Under the old predicate both read as configured, because the
        // comparison against Protect("") could never match anything.
        Assert.False(blank.IsExamToolsConfigured);
        Assert.True(real.IsExamToolsConfigured);
    }

    [Fact]
    public async Task ATeamWithNoPasswordAtAll_IsReportedAsNotConfigured()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Teams.Add(NewTeam("NOPW", null));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.False(Assert.Single(report.Teams).IsExamToolsConfigured);
    }

    /// <summary>
    /// The premise the test above rests on, asserted rather than assumed: the column really is
    /// encrypted, so a stored empty string really does become non-empty ciphertext on disk. If the
    /// converter were ever dropped from this property the tests would still pass while covering
    /// something else entirely.
    /// </summary>
    [Fact]
    public async Task TheStoredPasswordIsCiphertext_NotThePlaintextItWasGiven()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.Teams.Add(NewTeam("REALPW", "secret"));
        await dbContext.SaveChangesAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExamToolsPassword FROM Teams WHERE Name = 'REALPW'";
        var raw = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(raw);
        Assert.NotEqual("secret", raw);
        Assert.True(raw!.Length > "secret".Length, "stored value does not look like ciphertext");
    }
}
