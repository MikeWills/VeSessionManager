using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class VolunteerExaminerReportServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string teamCode = "TESTTEAM")
    {
        var team = new Team { Name = teamCode, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Vec> SeedVecAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        return vec;
    }

    private static async Task<FeeConfiguration> SeedFeeConfigurationAsync(AppDbContext dbContext, Vec vec)
    {
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
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        return feeConfiguration;
    }

    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, Vec vec, FeeConfiguration feeConfiguration,
        DateTime scheduledStartUtc, SessionStatus status = SessionStatus.Active, string? examToolsSessionId = null)
    {
        var session = new Session
        {
            ExamToolsSessionId = examToolsSessionId ?? Guid.NewGuid().ToString(),
            Title = "Test Session",
            ScheduledStartUtc = scheduledStartUtc,
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            Status = status,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, Team team, string callSign, string name)
    {
        var ve = new VolunteerExaminer { Name = name, CallSign = callSign, Team = team };
        dbContext.VolunteerExaminers.Add(ve);
        await dbContext.SaveChangesAsync();
        return ve;
    }

    private static void Link(AppDbContext dbContext, Session session, VolunteerExaminer ve) =>
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = ve });

    [Fact]
    public async Task CountsSessionsPerVe_Correctly()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var sessionA = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var sessionB = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));
        var veLead = await SeedVeAsync(dbContext, team, "N2SPG", "Lead VE");
        var veCoLead = await SeedVeAsync(dbContext, team, "NP2UU", "Co-Lead VE");
        Link(dbContext, sessionA, veLead);
        Link(dbContext, sessionA, veCoLead);
        Link(dbContext, sessionB, veLead);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, CancellationToken.None);

        Assert.Equal(2, counts.Count);
        var lead = counts.Single(c => c.VolunteerExaminerId == veLead.Id);
        var coLead = counts.Single(c => c.VolunteerExaminerId == veCoLead.Id);
        Assert.Equal(2, lead.SessionCount);
        Assert.Equal(1, coLead.SessionCount);
        Assert.Equal("N2SPG", lead.CallSign);
    }

    [Fact]
    public async Task DateRangeFilter_ExcludesSessionsOutsideRange_InclusiveOfBoundaries()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var before = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 5, 31, 23, 59, 59, DateTimeKind.Utc));
        var onFrom = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var onTo = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));
        var after = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 30, 0, 0, 1, DateTimeKind.Utc));
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, before, ve);
        Link(dbContext, onFrom, ve);
        Link(dbContext, onTo, ve);
        Link(dbContext, after, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext).GetSessionCountsAsync(
            [team.Id],
            fromUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            toUtc: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        var result = Assert.Single(counts);
        Assert.Equal(2, result.SessionCount); // onFrom and onTo only
    }

    [Fact]
    public async Task CancelledSessions_AreExcludedFromCounts()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var active = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var cancelled = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), SessionStatus.Cancelled);
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, active, ve);
        Link(dbContext, cancelled, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, CancellationToken.None);

        var result = Assert.Single(counts);
        Assert.Equal(1, result.SessionCount);
    }

    [Fact]
    public async Task OnlyCountsTheGivenTeamsSessions()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var veA = await SeedVeAsync(dbContext, teamA, "N2SPG", "Team A's VE");
        var veB = await SeedVeAsync(dbContext, teamB, "N2SPG", "Team B's VE"); // same callsign, different team
        Link(dbContext, sessionA, veA);
        Link(dbContext, sessionB, veB);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([teamA.Id], fromUtc: null, toUtc: null, CancellationToken.None);

        var result = Assert.Single(counts);
        Assert.Equal(veA.Id, result.VolunteerExaminerId);
        Assert.Equal("Team A's VE", result.Name);
    }

    // ---- "All teams" (2026-07-30) ----
    // teamIds is now a set, with null meaning every team — backs the VE Roster page's "All teams"
    // option, matching the session list.

    [Fact]
    public async Task NullTeamIds_CountsEveryTeam_WithEachVeStillAttributedToItsOwnTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var veA = await SeedVeAsync(dbContext, teamA, "N2SPG", "Team A's VE");
        var veB = await SeedVeAsync(dbContext, teamB, "N2SPG", "Team B's VE"); // same callsign, different team
        Link(dbContext, sessionA, veA);
        Link(dbContext, sessionB, veB);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync(null, fromUtc: null, toUtc: null, CancellationToken.None);

        // Two rows, not one merged row — a VolunteerExaminer is team-scoped, so the same callsign in
        // two teams is two different records and must not be silently combined.
        Assert.Equal(2, counts.Count);
        Assert.Equal(["TEAMA", "TEAMB"], counts.Select(c => c.TeamName).OrderBy(n => n));
    }

    [Fact]
    public async Task MultipleTeamIds_CountsOnlyThoseTeams()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        foreach (var (team, name) in new[] { (teamA, "A"), (teamB, "B"), (teamC, "C") })
        {
            var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Link(dbContext, session, await SeedVeAsync(dbContext, team, $"CALL{name}", $"VE {name}"));
        }
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([teamA.Id, teamC.Id], fromUtc: null, toUtc: null, CancellationToken.None);

        Assert.Equal(["TEAMA", "TEAMC"], counts.Select(c => c.TeamName).OrderBy(n => n));
    }
}
