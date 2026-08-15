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

    /// <summary>
    /// Defaults to a <b>completed</b> session, because that is what the report counts — the other
    /// tests here are about grouping, date ranges and team scoping, and would all silently assert
    /// zero if their sessions were merely scheduled. Pass <c>completed: false</c> for the cases that
    /// are specifically about the completion rule.
    /// </summary>
    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, Vec vec, FeeConfiguration feeConfiguration,
        DateTime scheduledStartUtc, SessionStatus status = SessionStatus.Active, string? examToolsSessionId = null,
        bool completed = true)
    {
        var session = new Session
        {
            // The upstream-closed route, the one historical imports and normal ingestion both use.
            ExamToolsClosedUtc = completed ? scheduledStartUtc.AddHours(3) : null,
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
        var ve = new VolunteerExaminer { Name = name, CallSign = callSign };
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
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

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
            toUtc: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), search: null, CancellationToken.None);

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
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

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
            .GetSessionCountsAsync([teamA.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

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
            .GetSessionCountsAsync(null, fromUtc: null, toUtc: null, search: null, CancellationToken.None);

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
            .GetSessionCountsAsync([teamA.Id, teamC.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Equal(["TEAMA", "TEAMC"], counts.Select(c => c.TeamName).OrderBy(n => n));
    }

    // ---- Completed sessions only (2026-08-06) ----------------------------------------------------
    // "Sessions worked" counted every non-cancelled session, which includes ones still in the future:
    // Status only ever leaves Active on cancellation, it is never set to Completed. A VE rostered onto
    // next month's session already had it in their total.

    [Fact]
    public async Task ScheduledButNotYetHeldSession_IsNotCountedAsWorked()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var held = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var upcoming = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), completed: false);
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, held, ve);
        Link(dbContext, upcoming, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Equal(1, Assert.Single(counts).SessionCount);
    }

    /// <summary>Both completion routes count — a Session Manager marking it, or ExamTools closing it.</summary>
    [Fact]
    public async Task ManuallyMarkedCompleteSession_CountsEvenWithoutTheExamToolsStamp()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), completed: false);
        session.TestingCompletedUtc = new DateTime(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc);
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, session, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Equal(1, Assert.Single(counts).SessionCount);
    }

    /// <summary>
    /// A VE whose only session is still upcoming drops off the roster report entirely rather than
    /// showing a zero — the query groups over the rows that survive the filter.
    /// </summary>
    [Fact]
    public async Task VeWithOnlyUpcomingSessions_DoesNotAppear()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var upcoming = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), completed: false);
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, upcoming, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Empty(counts);
    }

    /// <summary>A cancelled session is excluded even though ingestion stamped it closed upstream.</summary>
    [Fact]
    public async Task CancelledSession_IsStillExcluded_EvenWhenClosedUpstream()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var feeConfiguration = await SeedFeeConfigurationAsync(dbContext, vec);
        var cancelled = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), SessionStatus.Cancelled);
        var ve = await SeedVeAsync(dbContext, team, "N2SPG", "Test VE");
        Link(dbContext, cancelled, ve);
        await dbContext.SaveChangesAsync();

        var counts = await new VolunteerExaminerReportService(dbContext)
            .GetSessionCountsAsync([team.Id], fromUtc: null, toUtc: null, search: null, CancellationToken.None);

        Assert.Empty(counts);
    }

    // ---- Paging (2026-08-15) ----

    /// <summary>
    /// Seeds VEs with distinct session counts, 1 up to <paramref name="veCount"/>, so the ranking is
    /// unambiguous and "page 2 continues where page 1 stopped" is checkable rather than a vibe.
    ///
    /// <para><b>Creation order is the inverse of rank, deliberately: the busiest VE is inserted
    /// last.</b> The first version of this helper created them busiest-first, which made row id
    /// order coincide exactly with session-count order — so a pager that sorted by id instead of by
    /// count passed every test here. That mutation was run and did <i>not</i> fail, which is how the
    /// flaw was found. Any correlation between insertion order and the property under test makes
    /// these assertions decorative, so keep them opposed.</para>
    ///
    /// <para>Names carry the count (<c>VE 07</c> works 7 sessions) so a failure message says what
    /// went wrong without needing the seed re-read.</para>
    /// </summary>
    private static async Task<Team> SeedRankedVesAsync(AppDbContext dbContext, int veCount)
    {
        var team = await SeedTeamAsync(dbContext);
        var vec = await SeedVecAsync(dbContext);
        var fee = await SeedFeeConfigurationAsync(dbContext, vec);

        var sessions = new List<Session>();
        for (var i = 0; i < veCount; i++)
        {
            sessions.Add(await SeedSessionAsync(
                dbContext, team, vec, fee, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)));
        }

        // Ascending: the first VE created works 1 session, the last works veCount.
        for (var worked = 1; worked <= veCount; worked++)
        {
            var ve = await SeedVeAsync(dbContext, team, $"K0{worked:00}", $"VE {worked:00}");
            for (var s = 0; s < worked; s++)
            {
                Link(dbContext, sessions[s], ve);
            }
        }

        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task Page_ReturnsOnlyThatPage_ButCountsEveryMatchingRow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedRankedVesAsync(dbContext, veCount: 12);

        var page = await new VolunteerExaminerReportService(dbContext).GetSessionCountsPageAsync(
            [team.Id], null, null, null, pageNumber: 1, pageSize: 5, CancellationToken.None);

        Assert.Equal(5, page.Rows.Count);
        Assert.Equal(12, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(1, page.PageNumber);
    }

    /// <summary>
    /// The property that catches slicing an unordered set: page 2 must continue the ranking, not
    /// restart it or repeat page 1. Ordering happens client-side here (the InMemory GroupBy/OrderBy
    /// constraint), so a Skip/Take pushed into the query would slice before the sort and hand back
    /// arbitrary rows — which still looks like a working pager until you read the numbers.
    /// </summary>
    [Fact]
    public async Task Page_ContinuesTheRanking_RatherThanRestartingIt()
    {
        await using var dbContext = CreateContext();
        var team = await SeedRankedVesAsync(dbContext, veCount: 12);
        var service = new VolunteerExaminerReportService(dbContext);

        var first = await service.GetSessionCountsPageAsync([team.Id], null, null, null, 1, 5, CancellationToken.None);
        var second = await service.GetSessionCountsPageAsync([team.Id], null, null, null, 2, 5, CancellationToken.None);

        // Busiest first, and strictly descending across the page boundary.
        Assert.Equal(12, first.Rows[0].SessionCount);
        Assert.Equal(8, first.Rows[4].SessionCount);
        Assert.Equal(7, second.Rows[0].SessionCount);

        // And no row appears on both pages.
        Assert.Empty(first.Rows.Select(r => r.VolunteerExaminerId)
            .Intersect(second.Rows.Select(r => r.VolunteerExaminerId)));
    }

    /// <summary>A stale link to a page that no longer exists lands on the last page, not on an empty one.</summary>
    [Fact]
    public async Task Page_PastTheEnd_ClampsToTheLastPage()
    {
        await using var dbContext = CreateContext();
        var team = await SeedRankedVesAsync(dbContext, veCount: 12);

        var page = await new VolunteerExaminerReportService(dbContext).GetSessionCountsPageAsync(
            [team.Id], null, null, null, pageNumber: 99, pageSize: 5, CancellationToken.None);

        Assert.Equal(3, page.PageNumber);
        Assert.Equal(2, page.Rows.Count);
        Assert.NotEmpty(page.Rows);
    }

    [Fact]
    public async Task Page_BelowOne_ClampsToTheFirstPage()
    {
        await using var dbContext = CreateContext();
        var team = await SeedRankedVesAsync(dbContext, veCount: 12);

        var page = await new VolunteerExaminerReportService(dbContext).GetSessionCountsPageAsync(
            [team.Id], null, null, null, pageNumber: 0, pageSize: 5, CancellationToken.None);

        Assert.Equal(1, page.PageNumber);
        Assert.Equal(12, page.Rows[0].SessionCount);
        Assert.Equal("VE 12", page.Rows[0].Name);
    }

    /// <summary>Empty must still report one page — a "Page 1 of 0" is nonsense, and dividing by it downstream is worse.</summary>
    [Fact]
    public async Task Page_WithNoMatchingRows_ReportsOnePageAndNoRows()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var page = await new VolunteerExaminerReportService(dbContext).GetSessionCountsPageAsync(
            [team.Id], null, null, null, pageNumber: 1, pageSize: 25, CancellationToken.None);

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.Equal(1, page.PageNumber);
    }

    /// <summary>Paging must not quietly widen the filters — the count is of matching rows, not of everyone.</summary>
    [Fact]
    public async Task Page_AppliesTheSearchFilterBeforePaging()
    {
        await using var dbContext = CreateContext();
        var team = await SeedRankedVesAsync(dbContext, veCount: 12);

        var page = await new VolunteerExaminerReportService(dbContext).GetSessionCountsPageAsync(
            [team.Id], null, null, "VE 07", pageNumber: 1, pageSize: 25, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("VE 07", Assert.Single(page.Rows).Name);
    }
}
