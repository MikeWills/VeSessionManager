using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The alert bell's feed (#339). Three properties are worth pinning, and each has already been got
/// wrong somewhere else in this app:
///
/// <para><b>Role.</b> An alert links straight to the page the problem lives on, so a feed that hands
/// a SessionManager a reconciliation alert is offering a link that 403s — the exact bug the nav's
/// own gates were added to fix. The gate lives here rather than only in the partial because the
/// service is what decides an alert exists at all.</para>
///
/// <para><b>teamIds null-vs-empty.</b> Same convention as <see cref="NavBadgeCountService"/>: null is
/// "every team" (SystemAdmin), an empty list is "no teams". Backwards means a silently empty bell
/// for the one role that should see everything.</para>
///
/// <para><b>The overflow count.</b> The menu shows at most a handful, but the badge must count them
/// all — a bell reading "5" beside a page listing 40 is worse than no bell.</para>
/// </summary>
public class AlertFeedServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<ReconciliationFinding> SeedFindingAsync(
        AppDbContext dbContext,
        Team team,
        DateTime? resolvedUtc = null,
        DateTime? firstSeenUtc = null,
        ReconciliationFindingKind kind = ReconciliationFindingKind.MissingSession)
    {
        var finding = new ReconciliationFinding
        {
            TeamId = team.Id,
            Kind = kind,
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            SessionDateUtc = new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc),
            Detail = "ExamTools has a closed session this app never ingested.",
            FirstSeenUtc = firstSeenUtc ?? Now,
            LastSeenUtc = firstSeenUtc ?? Now,
            ResolvedUtc = resolvedUtc
        };
        dbContext.ReconciliationFindings.Add(finding);
        await dbContext.SaveChangesAsync();
        return finding;
    }

    [Fact]
    public async Task SystemAdmin_WithNullTeamIds_SeesOpenFindingsFromEveryTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedFindingAsync(dbContext, teamA);
        await SeedFindingAsync(dbContext, teamB);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Equal(2, feed.TotalCount);
        Assert.Equal(2, feed.Items.Count);
        Assert.All(feed.Items, item => Assert.Equal("/Admin/Reconciliation", item.PageName));
    }

    [Fact]
    public async Task ScopedToOneTeam_ExcludesAnotherTeamsFindings()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedFindingAsync(dbContext, teamA);
        await SeedFindingAsync(dbContext, teamB);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.TeamAdmin, [teamA.Id], CancellationToken.None);

        var item = Assert.Single(feed.Items);
        Assert.Equal("TEAM-A", item.TeamName);
    }

    [Fact]
    public async Task EmptyTeamIds_SeesNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedFindingAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.TeamAdmin, [], CancellationToken.None);

        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.TotalCount);
    }

    [Fact]
    public async Task ResolvedFindings_AreNotAlerts()
    {
        // The page keeps resolved rows on purpose ("this was wrong and is now fixed"); the bell is
        // about what still needs somebody, so a resolved row must leave it.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedFindingAsync(dbContext, team, resolvedUtc: Now);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.TotalCount);
    }

    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task NonAdminRoles_GetNoReconciliationAlerts(UserRole role)
    {
        // Reconciliation.cshtml.cs is [Authorize(Roles = RoleGroups.Admins)] — an alert offered to
        // these roles would be a link straight to a 403. AlertPageRoleGateTests keeps the two in step.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedFindingAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(role, [team.Id], CancellationToken.None);

        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.TotalCount);
    }

    [Fact]
    public async Task MoreFindingsThanTheMenuShows_CountsThemAllButListsAtMostMaxItems()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        for (var i = 0; i < AlertFeedService.MaxItems + 3; i++)
        {
            await SeedFindingAsync(dbContext, team, firstSeenUtc: Now.AddDays(-i));
        }

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Equal(AlertFeedService.MaxItems + 3, feed.TotalCount);
        Assert.Equal(AlertFeedService.MaxItems, feed.Items.Count);
        Assert.True(feed.HasMore);
    }

    [Fact]
    public async Task NewestFindingIsListedFirst()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedFindingAsync(dbContext, team, firstSeenUtc: Now.AddDays(-9), kind: ReconciliationFindingKind.CandidateCountMismatch);
        var newest = await SeedFindingAsync(dbContext, team, firstSeenUtc: Now);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Equal(newest.Id, feed.Items[0].HighlightId);
    }

    [Fact]
    public async Task EachAlertCarriesTheRowItPointsAt()
    {
        // The whole ask in #339: the alert navigates to *where the alert is*, which means the
        // finding's own id travels with it rather than the reader landing on an unfiltered list.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var finding = await SeedFindingAsync(dbContext, team, kind: ReconciliationFindingKind.CandidateCountMismatch);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        var item = Assert.Single(feed.Items);
        Assert.Equal(finding.Id, item.HighlightId);
        Assert.Equal("/Admin/Reconciliation", item.PageName);
        Assert.Equal(finding.Detail, item.Detail);
        Assert.Equal("TEAM-A", item.TeamName);
    }
}
