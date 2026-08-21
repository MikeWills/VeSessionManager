using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The upgrade path for a team that already existed when messages stopped pointing at templates.
///
/// <para><c>MessagesOwnTheirContent</c> deletes every rule; <c>Team.MessageRulesSeededUtc</c> says
/// "already set up, never seed again". Together they leave an existing team with <b>no messages at
/// all, permanently</b>, while a brand-new team gets seven. Nothing fails, nothing logs, and the
/// Messages page reads as broken rather than as a fresh start.</para>
///
/// <para><b>Real SQLite, because the fix is raw SQL in a migration</b> — a correlated
/// <c>NOT IN (SELECT …)</c> that the in-memory provider cannot run at all.</para>
/// </summary>
public class MessageReseedSqliteTests
{
    /// <summary>The migration's statement, kept identical so this tests the shipped SQL rather than a paraphrase of it.</summary>
    private const string ReseedSql =
        """
        UPDATE Teams
        SET MessageRulesSeededUtc = NULL
        WHERE Id NOT IN (SELECT DISTINCT TeamId FROM MessageRules);
        """;

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name, DateTime? seededUtc)
    {
        var team = new Team { Name = name, CreatedUtc = DateTime.UtcNow, MessageRulesSeededUtc = seededUtc };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>
    /// The case this exists for: a team carried through the content refactor, stamped as seeded and
    /// left with nothing. It must get the examples back.
    /// </summary>
    [Fact]
    public async Task ATeamLeftWithNoMessages_IsSeededAgain()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext, "EXISTING", seededUtc: new DateTime(2026, 8, 17, 11, 24, 27, DateTimeKind.Utc));

        await dbContext.Database.ExecuteSqlRawAsync(ReseedSql);
        dbContext.ChangeTracker.Clear();

        var reloaded = await dbContext.Teams.FirstAsync(t => t.Id == team.Id);
        Assert.Null(reloaded.MessageRulesSeededUtc);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, reloaded);
        await dbContext.SaveChangesAsync();

        Assert.Equal(7, await dbContext.MessageRules.CountAsync(r => r.TeamId == team.Id));
        Assert.NotNull((await dbContext.Teams.FirstAsync(t => t.Id == team.Id)).MessageRulesSeededUtc);
    }

    /// <summary>
    /// ⚠️ The reason the statement is scoped rather than a blanket update. A team created in the
    /// window between the two migrations already has its seven; clearing its tombstone would give it
    /// fourteen, which is worse than the problem being fixed.
    /// </summary>
    [Fact]
    public async Task ATeamThatAlreadyHasMessages_IsLeftAlone()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var stamped = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);
        var team = await SeedTeamAsync(dbContext, "ALREADYSET", seededUtc: stamped);

        dbContext.MessageRules.Add(new MessageRule
        {
            TeamId = team.Id,
            Name = "Something the team wrote",
            Trigger = MessageTrigger.CandidateRegistered,
            Subject = "Subject",
            Body = "<p>Body</p>",
            CreatedUtc = stamped
        });
        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlRawAsync(ReseedSql);
        dbContext.ChangeTracker.Clear();

        var reloaded = await dbContext.Teams.FirstAsync(t => t.Id == team.Id);
        Assert.Equal(stamped, reloaded.MessageRulesSeededUtc);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, reloaded);
        await dbContext.SaveChangesAsync();

        // Still one. The tombstone did its job.
        Assert.Equal(1, await dbContext.MessageRules.CountAsync(r => r.TeamId == team.Id));
    }

    /// <summary>
    /// A team that deliberately deleted every message is indistinguishable from one the refactor
    /// emptied, so this re-seeds it too.
    ///
    /// <para>Accepted rather than solved: nothing records the difference between "we send nothing on
    /// purpose" and "the migration took them". The cost is one team turning off seven examples again;
    /// the alternative cost is every existing team silently sending nothing forever. If it matters
    /// later, the fix is a column recording why the tombstone was cleared, not a cleverer predicate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATeamThatDeletedEverythingOnPurpose_IsAlsoReseeded_AndThatIsAccepted()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext, "SENDSNOTHING", seededUtc: new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));

        await dbContext.Database.ExecuteSqlRawAsync(ReseedSql);
        dbContext.ChangeTracker.Clear();

        Assert.Null((await dbContext.Teams.FirstAsync(t => t.Id == team.Id)).MessageRulesSeededUtc);
    }
}
