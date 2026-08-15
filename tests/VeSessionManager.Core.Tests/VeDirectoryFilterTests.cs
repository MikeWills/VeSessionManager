using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The license-status and last-worked filters on the VE Directory (requested 2026-08-10).
///
/// <para>Last-worked is a property of the finished <i>row</i> — the maximum across the teams in
/// scope — so it is applied after the grouping, next to the guest filter and for the same reason.
/// License status is not: it is a property of the person, identical across their memberships, and
/// since #298 it is filtered in SQL so the directory can page.</para>
///
/// <para><b>Real SQLite, not InMemory, since #298.</b> The license filter uses <c>GLOB</c> for the
/// call-sign shape test, which InMemory cannot run — it fails with "the query has switched to
/// client-evaluation". That is the provider-dependence CLAUDE.md warns about, arriving on schedule:
/// a query written to be translated has to be tested against something that translates it.</para>
/// </summary>
public class VeDirectoryFilterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A `:memory:` database lives exactly as long as its connection, so each test's is held
    /// open here and closed when the class is disposed.</summary>
    private readonly List<SqliteConnection> connections = [];

    public void Dispose()
    {
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
    }

    private AppDbContext CreateContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        connections.Add(connection);

        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, Team team, string callSign, DateTime? licenseExpiresUtc, DateTime? lastCheckedUtc)
    {
        var person = new VolunteerExaminer
        {
            Name = callSign,
            CallSign = callSign,
            LicenseExpiresUtc = licenseExpiresUtc,
            LicenseLastCheckedUtc = lastCheckedUtc,
            OperatorClass = LicenseClass.Extra
        };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true });
        await dbContext.SaveChangesAsync();
        return person;
    }

    /// <summary>
    /// A Session needs a Vec and a FeeConfiguration, and real SQLite enforces both — InMemory did not,
    /// which is why these rows were absent until #298 moved this file onto a real provider. Created
    /// once and reused.
    /// </summary>
    private static async Task<(int VecId, int FeeConfigurationId)> EnsureSessionPrerequisitesAsync(AppDbContext dbContext)
    {
        var existing = await dbContext.FeeConfigurations.FirstOrDefaultAsync();
        if (existing is not null)
        {
            return (existing.VecId, existing.Id);
        }

        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = false,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        return (vec.Id, feeConfiguration.Id);
    }

    private static async Task SeedWorkedSessionAsync(AppDbContext dbContext, Team team, VolunteerExaminer person, DateTime startUtc)
    {
        var (vecId, feeConfigurationId) = await EnsureSessionPrerequisitesAsync(dbContext);
        var session = new Session
        {
            TeamId = team.Id,
            VecId = vecId,
            FeeConfigurationId = feeConfigurationId,
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            ScheduledStartUtc = startUtc,
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = startUtc.AddHours(3)
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = person.Id });
        await dbContext.SaveChangesAsync();
    }

    private static Task<IReadOnlyList<VeDirectoryRow>> QueryAsync(AppDbContext dbContext, VeDirectoryFilter filter) =>
        new VolunteerExaminerDirectoryService(dbContext).GetDirectoryAsync(null, filter, Now, CancellationToken.None);

    // ---- license status ----

    [Fact]
    public async Task FilteringByLicenseStatusMatchesTheStatusTheRowDisplays()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVeAsync(dbContext, team, "N2SPG", Now.AddYears(5), Now);     // comfortably Active
        await SeedVeAsync(dbContext, team, "W7QQQ", Now.AddDays(30), Now);     // inside the 90-day renewal window
        await SeedVeAsync(dbContext, team, "K4ZZZ", null, null);               // never checked

        var active = await QueryAsync(dbContext, new VeDirectoryFilter { LicenseStatus = WatchedLicenseStatus.Active });
        Assert.Equal("N2SPG", Assert.Single(active).VolunteerExaminer.CallSign);

        var expiring = await QueryAsync(dbContext, new VeDirectoryFilter { LicenseStatus = WatchedLicenseStatus.ExpiringSoon });
        Assert.Equal("W7QQQ", Assert.Single(expiring).VolunteerExaminer.CallSign);

        var unchecked_ = await QueryAsync(dbContext, new VeDirectoryFilter { LicenseStatus = WatchedLicenseStatus.NotYetChecked });
        Assert.Equal("K4ZZZ", Assert.Single(unchecked_).VolunteerExaminer.CallSign);

        Assert.Equal(3, (await QueryAsync(dbContext, new VeDirectoryFilter())).Count);
    }

    // ---- last worked ----

    [Fact]
    public async Task WorkedSinceReturnsOnlyThoseInsideTheWindow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var recent = await SeedVeAsync(dbContext, team, "N2SPG", Now.AddYears(5), Now);
        var old = await SeedVeAsync(dbContext, team, "W7QQQ", Now.AddYears(5), Now);
        await SeedWorkedSessionAsync(dbContext, team, recent, Now.AddDays(-40));
        await SeedWorkedSessionAsync(dbContext, team, old, Now.AddDays(-400));

        var lastThreeMonths = await QueryAsync(dbContext, new VeDirectoryFilter { WorkedFromUtc = Now.AddMonths(-3) });

        Assert.Equal("N2SPG", Assert.Single(lastThreeMonths).VolunteerExaminer.CallSign);
    }

    /// <summary>
    /// "Over a year ago" is an upper bound where every other option is a lower bound — it is the one
    /// that answers "who has gone quiet", and the one whose direction is easiest to invert by
    /// accident.
    /// </summary>
    [Fact]
    public async Task OverAYearAgoReturnsTheOnesWhoHaveGoneQuiet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var recent = await SeedVeAsync(dbContext, team, "N2SPG", Now.AddYears(5), Now);
        var quiet = await SeedVeAsync(dbContext, team, "W7QQQ", Now.AddYears(5), Now);
        await SeedWorkedSessionAsync(dbContext, team, recent, Now.AddDays(-40));
        await SeedWorkedSessionAsync(dbContext, team, quiet, Now.AddDays(-400));

        var goneQuiet = await QueryAsync(dbContext, new VeDirectoryFilter { WorkedToUtc = Now.AddYears(-1) });

        Assert.Equal("W7QQQ", Assert.Single(goneQuiet).VolunteerExaminer.CallSign);
    }

    /// <summary>
    /// Someone who has never worked a session satisfies NEITHER bound: both are claims about a date
    /// that does not exist. A hand-added prospect therefore appears only in the unfiltered list, which
    /// is the honest answer rather than filing them under "gone quiet".
    /// </summary>
    [Fact]
    public async Task SomeoneWhoHasNeverWorkedMatchesNoTimeFilter()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVeAsync(dbContext, team, "K4ZZZ", Now.AddYears(5), Now);   // a prospect: no sessions

        Assert.Empty(await QueryAsync(dbContext, new VeDirectoryFilter { WorkedFromUtc = Now.AddMonths(-3) }));
        Assert.Empty(await QueryAsync(dbContext, new VeDirectoryFilter { WorkedToUtc = Now.AddYears(-1) }));
        Assert.Single(await QueryAsync(dbContext, new VeDirectoryFilter()));
    }

    [Fact]
    public async Task ACustomRangeBoundsBothEnds()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var inside = await SeedVeAsync(dbContext, team, "N2SPG", Now.AddYears(5), Now);
        var before = await SeedVeAsync(dbContext, team, "W7QQQ", Now.AddYears(5), Now);
        var after = await SeedVeAsync(dbContext, team, "K4ZZZ", Now.AddYears(5), Now);
        await SeedWorkedSessionAsync(dbContext, team, inside, Now.AddDays(-100));
        await SeedWorkedSessionAsync(dbContext, team, before, Now.AddDays(-300));
        await SeedWorkedSessionAsync(dbContext, team, after, Now.AddDays(-10));

        var rows = await QueryAsync(dbContext, new VeDirectoryFilter
        {
            WorkedFromUtc = Now.AddDays(-200),
            WorkedToUtc = Now.AddDays(-50)
        });

        Assert.Equal("N2SPG", Assert.Single(rows).VolunteerExaminer.CallSign);
    }

    /// <summary>Filters narrow together rather than replacing one another — the case a UI makes easy to get wrong.</summary>
    [Fact]
    public async Task FiltersCombine()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var wanted = await SeedVeAsync(dbContext, team, "N2SPG", Now.AddYears(5), Now);       // Active, recent
        var wrongStatus = await SeedVeAsync(dbContext, team, "W7QQQ", Now.AddDays(30), Now);  // ExpiringSoon, recent
        await SeedWorkedSessionAsync(dbContext, team, wanted, Now.AddDays(-10));
        await SeedWorkedSessionAsync(dbContext, team, wrongStatus, Now.AddDays(-10));

        var rows = await QueryAsync(dbContext, new VeDirectoryFilter
        {
            LicenseStatus = WatchedLicenseStatus.Active,
            WorkedFromUtc = Now.AddMonths(-3)
        });

        Assert.Equal("N2SPG", Assert.Single(rows).VolunteerExaminer.CallSign);
    }
}
