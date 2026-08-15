using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The batched CSV import (#294) — one query for every matched person and one save for the whole
/// file, replacing a per-row <c>FirstAsync</c> plus two saves that made a 176-row import roughly 500
/// round trips.
///
/// <para><b>Real SQLite, not InMemory, and that is the whole reason this file exists separately from
/// VolunteerExaminerImportServiceTests.</b> Batching means a brand-new person's membership is
/// attached through the navigation property while their <c>Id</c> is still 0, and EF fills the
/// foreign key in from the relationship at save time. InMemory does not enforce foreign keys, so it
/// would report success whether or not that actually worked — the failure would appear only on the
/// deployment.</para>
/// </summary>
public class VeImportBatchSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        // The import writes an audit row, and real SQLite enforces its foreign key.
        dbContext.Users.Add(new User { Name = "Acting admin", Email = "admin@localhost", Role = UserRole.SystemAdmin });
        await dbContext.SaveChangesAsync();
        return (connection, dbContext);
    }

    private static VolunteerExaminerImportService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private const string Header = "CallSign,Name,Email,Phone,City";

    /// <summary>
    /// The property batching could plausibly break: every created person must end up with a
    /// membership pointing at them, with a foreign key the database accepts. Under the old
    /// save-per-row code the id existed before the membership was built; now it does not.
    /// </summary>
    [Fact]
    public async Task ManyNewRows_EachGetAMembershipWithARealForeignKey()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");

        var rows = Enumerable.Range(0, 25).Select(i => $"K0AA{i:D2},Person {i},p{i}@example.com,,Mankato");
        var csv = Header + "\n" + string.Join("\n", rows);

        var result = await CreateService(dbContext).ApplyAsync(csv, team.Id, userId: 1, CancellationToken.None);

        Assert.Equal(25, result.Created);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        var people = await verify.VolunteerExaminers.AsNoTracking().ToListAsync();
        Assert.Equal(25, people.Count);

        var memberships = await verify.VeTeamMemberships.AsNoTracking().ToListAsync();
        Assert.Equal(25, memberships.Count);

        // The assertion that matters: no membership points at id 0, and each names a real person.
        Assert.DoesNotContain(memberships, m => m.VolunteerExaminerId == 0);
        var personIds = people.Select(p => p.Id).ToHashSet();
        Assert.All(memberships, m => Assert.Contains(m.VolunteerExaminerId, personIds));
        Assert.All(memberships, m => Assert.Equal(team.Id, m.TeamId));
    }

    /// <summary>
    /// A person already on this team must not gain a second membership — the unique index on
    /// (VolunteerExaminerId, TeamId) is real in SQLite and would throw, which is exactly the guard
    /// InMemory cannot provide.
    /// </summary>
    [Fact]
    public async Task ExistingMemberIsUpdated_NotGivenASecondMembership()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = new VolunteerExaminer { Name = "Sam Granger", CallSign = "N2SPG", CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(new VeTeamMembership
        {
            VolunteerExaminer = person, Team = team, IsActive = true, CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var csv = $"{Header}\nN2SPG,Sam Granger,sam@example.com,,Mankato";
        var result = await CreateService(dbContext).ApplyAsync(csv, team.Id, userId: 1, CancellationToken.None);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        Assert.Equal(1, await verify.VolunteerExaminers.CountAsync());
        Assert.Equal(1, await verify.VeTeamMemberships.CountAsync());
        Assert.Equal("sam@example.com", (await verify.VolunteerExaminers.AsNoTracking().SingleAsync()).Email);
    }

    /// <summary>
    /// A person on another team gains a membership here rather than a duplicate record — the mixed
    /// batch, where creates, updates and add-to-team all land in one save.
    /// </summary>
    [Fact]
    public async Task MixedBatch_Creates_Updates_AndAddsToTeam_InOneSave()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var hrcc = await SeedTeamAsync(dbContext, "HRCC");
        var marc = await SeedTeamAsync(dbContext, "MARC");

        var onThisTeam = new VolunteerExaminer { Name = "Sam Granger", CallSign = "N2SPG", CreatedUtc = Now };
        var onOtherTeam = new VolunteerExaminer { Name = "Dana Vale", CallSign = "W5CBW", CreatedUtc = Now };
        dbContext.VolunteerExaminers.AddRange(onThisTeam, onOtherTeam);
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = onThisTeam, Team = hrcc, IsActive = true, CreatedUtc = Now });
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = onOtherTeam, Team = marc, IsActive = true, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var csv = $"{Header}\nN2SPG,Sam Granger,sam@example.com,,\nW5CBW,Dana Vale,,,\nK0NEW,New Person,,,";
        var result = await CreateService(dbContext).ApplyAsync(csv, hrcc.Id, userId: 1, CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.AddedToTeam);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        Assert.Equal(3, await verify.VolunteerExaminers.CountAsync());

        // Dana keeps MARC and gains HRCC; nobody is duplicated.
        var danaMemberships = await verify.VeTeamMemberships.AsNoTracking()
            .Where(m => m.VolunteerExaminerId == onOtherTeam.Id).Select(m => m.TeamId).ToListAsync();
        Assert.Equal(2, danaMemberships.Count);
        Assert.Contains(hrcc.Id, danaMemberships);
        Assert.Contains(marc.Id, danaMemberships);
    }
}
