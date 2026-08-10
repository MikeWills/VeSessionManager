using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE Roster's "sessions worked" count, pinned against real SQLite rather than InMemory.
///
/// <para><b>Why this file exists separately from <see cref="VolunteerExaminerReportServiceTests"/>.</b>
/// That query aggregates in the database — a <c>GroupBy</c> whose ordering already had to be moved
/// client-side because InMemory couldn't translate it — and 2026-08-06 added a completion filter to
/// its <c>WHERE</c>. InMemory evaluates predicates as plain LINQ, so it would happily pass a query
/// that throws in production. Same reasoning as <see cref="ActiveCandidateCountSqliteTests"/>.</para>
/// </summary>
public class VeSessionCountSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(); // an in-memory DB lives only as long as its connection
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    /// <summary>Seeds one VE linked to three sessions: one closed upstream, one marked by hand, one still upcoming.</summary>
    private static async Task<(Team Team, VolunteerExaminer Ve)> SeedAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        // Real SQLite enforces the FKs InMemory ignores: a Session needs a FeeConfiguration, which
        // needs a Vec and a creating User.
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        };

        Session Session(DateTime startUtc, DateTime? closedUtc, DateTime? markedUtc) => new()
        {
            ExamToolsSessionId = "s-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Session",
            ScheduledStartUtc = startUtc,
            DurationMinutes = 120,
            Vec = vec,
            Team = team,
            FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = closedUtc,
            TestingCompletedUtc = markedUtc,
            CreatedUtc = Now
        };

        var closedUpstream = Session(Now.AddDays(-30), Now.AddDays(-30).AddHours(3), null);
        var markedByHand = Session(Now.AddDays(-14), null, Now.AddDays(-14).AddHours(3));
        var upcoming = Session(Now.AddDays(21), null, null);

        var ve = new VolunteerExaminer { Name = "Test VE", CallSign = "N2SPG" };
        dbContext.Sessions.AddRange(closedUpstream, markedByHand, upcoming);
        dbContext.VolunteerExaminers.Add(ve);
        foreach (var session in new[] { closedUpstream, markedByHand, upcoming })
        {
            dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = ve });
        }

        await dbContext.SaveChangesAsync();
        return (team, ve);
    }

    /// <summary>
    /// The whole aggregation runs against SQLite — if the completion filter or the GroupBy fails to
    /// translate, this is where it throws rather than on the live roster page.
    /// </summary>
    [Fact]
    public async Task SessionCounts_TranslateToSql_AndCountCompletedSessionsOnly()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, ve) = await SeedAsync(dbContext);

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        var count = Assert.Single(counts);
        Assert.Equal(ve.Id, count.VolunteerExaminerId);
        Assert.Equal(2, count.SessionCount); // the upcoming one is not worked
    }

    /// <summary>The date-range bounds still translate alongside the completion filter.</summary>
    [Fact]
    public async Task SessionCounts_WithDateRange_TranslateToSql()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, _unused) = await SeedAsync(dbContext);

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], Now.AddDays(-21), Now, search: null, CancellationToken.None);

        Assert.Equal(1, Assert.Single(counts).SessionCount); // only the hand-marked one falls in range
    }

    /// <summary>Null teamIds means every team, not none — the convention used across this app.</summary>
    [Fact]
    public async Task SessionCounts_AllTeams_TranslateToSql()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        await SeedAsync(dbContext);

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync(teamIds: null, fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Equal(2, Assert.Single(counts).SessionCount);
    }

    // ---- VE search (issue #135) ------------------------------------------------------------------
    // Pinned against SQLite specifically: case-insensitivity is the whole point of the feature and is
    // exactly what InMemory cannot verify. Plain LINQ Contains is culture-sensitive in memory, while
    // SQLite's instr() is case-SENSITIVE — so a test that passed on InMemory could still mean "n2spg"
    // finds nothing in production.

    private static async Task<Team> SeedTwoVesAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "s-search",
            Title = "Session",
            ScheduledStartUtc = Now.AddDays(-10),
            DurationMinutes = 120,
            Vec = vec,
            Team = team,
            FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = Now.AddDays(-10).AddHours(3),
            CreatedUtc = Now
        };

        var spg = new VolunteerExaminer { Name = "Sam Granger", CallSign = "N2SPG" };
        var uu = new VolunteerExaminer { Name = "Uma Unwin", CallSign = "NP2UU" };
        dbContext.Sessions.Add(session);
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = spg });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = uu });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<IReadOnlyList<VeSessionCount>> SearchAsync(AppDbContext dbContext, Team team, string? search) =>
        await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search, CancellationToken.None);

    [Theory]
    [InlineData("N2SPG")]      // exact call sign
    [InlineData("n2spg")]      // lower case — the case SQLite's instr() would miss
    [InlineData("2sp")]        // partial call sign, mid-string
    [InlineData("Sam")]        // name
    [InlineData("gran")]       // partial name, lower case
    [InlineData("  N2SPG  ")]  // trimmed
    public async Task Search_MatchesCallSignOrName_CaseInsensitively(string term)
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTwoVesAsync(dbContext);

        var counts = await SearchAsync(dbContext, team, term);

        Assert.Equal("N2SPG", Assert.Single(counts).CallSign);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSearch_IsNoFilter(string? term)
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTwoVesAsync(dbContext);

        var counts = await SearchAsync(dbContext, team, term);

        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public async Task Search_WithNoMatch_ReturnsNothing()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTwoVesAsync(dbContext);

        Assert.Empty(await SearchAsync(dbContext, team, "W1AW"));
    }

    /// <summary>
    /// A literal % must match nothing rather than acting as a wildcard — the reason this uses
    /// ToLower().Contains() instead of EF.Functions.Like, which has no escape-character overload.
    /// </summary>
    [Fact]
    public async Task Search_TreatsWildcardCharactersLiterally()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTwoVesAsync(dbContext);

        Assert.Empty(await SearchAsync(dbContext, team, "%"));
        Assert.Empty(await SearchAsync(dbContext, team, "_"));
    }

    /// <summary>Search composes with the date range rather than replacing it.</summary>
    [Fact]
    public async Task Search_CombinesWithTheDateRange()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTwoVesAsync(dbContext);

        var outOfRange = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], Now.AddDays(-2), Now, "N2SPG", CancellationToken.None);

        Assert.Empty(outOfRange); // the session is 10 days old
    }
}
