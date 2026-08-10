using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE detail page's sessions-worked block, against <b>real SQLite</b>.
///
/// <para><b>Why this exists.</b> The InMemory tests for the same method passed while the deployed
/// page returned a 500 (2026-08-10). The query grouped by team and asked for a <i>filtered</i>
/// aggregate inside the grouping — a count of the rows in each group matching a date predicate.
/// InMemory is LINQ-to-objects, so it evaluated that happily; the real provider has to translate it
/// to SQL and threw instead. Exactly the provider-dependent trap CLAUDE.md warns about: whether a
/// query translates at all cannot be verified on InMemory.</para>
///
/// <para>Any future change to that query needs to stay covered here, not only by the InMemory
/// tests.</para>
/// </summary>
public class VeSessionHistorySqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>
    /// Real SQLite enforces Session's foreign keys, so the VEC, fee configuration and acting user all
    /// have to exist — the InMemory tests get away with none of them.
    /// </summary>
    private static async Task SeedWorkedSessionAsync(
        AppDbContext dbContext, Team team, VolunteerExaminer person, DateTime startUtc, User user, Vec vec)
    {
        var session = new Session
        {
            Team = team,
            Vec = vec,
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            ScheduledStartUtc = startUtc,
            DurationMinutes = 60,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true,
                ExamFeeAmount = 15m,
                CreatedByUser = user,
                CreatedUtc = Now
            },
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = startUtc.AddHours(3),
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = person.Id });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<(User User, Vec Vec)> SeedSupportingRowsAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Acting admin", Email = "admin@localhost", Role = UserRole.SystemAdmin };
        var vec = new Vec { Name = "ARRL" };
        dbContext.Users.Add(user);
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        return (user, vec);
    }

    /// <summary>The regression test for the 500: the query has to actually run on the real provider.</summary>
    [Fact]
    public async Task TheHistoryQueryRunsOnRealSqliteAndSplitsByTeam()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var (user, vec) = await SeedSupportingRowsAsync(dbContext);
        var hrcc = await SeedTeamAsync(dbContext, "HRCC");
        var marc = await SeedTeamAsync(dbContext, "MARC");
        var person = new VolunteerExaminer { Name = "Sam Granger", CallSign = "N2SPG" };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();

        await SeedWorkedSessionAsync(dbContext, hrcc, person, Now.AddDays(-10), user, vec);
        await SeedWorkedSessionAsync(dbContext, hrcc, person, Now.AddDays(-20), user, vec);
        await SeedWorkedSessionAsync(dbContext, hrcc, person, new DateTime(2025, 6, 1, 18, 0, 0, DateTimeKind.Utc), user, vec);
        await SeedWorkedSessionAsync(dbContext, marc, person, Now.AddDays(-5), user, vec);

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, Now, 5, CancellationToken.None);

        Assert.Equal(4, history.Total);
        Assert.Equal(3, history.ThisYear);
        Assert.Equal(2026, history.Year);

        var hrccRow = history.ByTeam.Single(t => t.TeamName == "HRCC");
        Assert.Equal(3, hrccRow.Total);
        Assert.Equal(2, hrccRow.ThisYear);
        Assert.Equal(1, history.ByTeam.Single(t => t.TeamName == "MARC").Total);

        Assert.Equal(4, history.Recent.Count);
        Assert.Equal(Now.AddDays(-5), history.Recent[0].ScheduledStartUtc);
    }

    /// <summary>A VE with no sessions at all is the first thing anyone opens after adding a prospect by hand.</summary>
    [Fact]
    public async Task AVeWithNoSessionsReturnsAnEmptyHistory()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var person = new VolunteerExaminer { Name = "New Prospect", CallSign = "K4ZZZ" };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, Now, 5, CancellationToken.None);

        Assert.Equal(0, history.Total);
        Assert.Empty(history.ByTeam);
        Assert.Empty(history.Recent);
    }
}
