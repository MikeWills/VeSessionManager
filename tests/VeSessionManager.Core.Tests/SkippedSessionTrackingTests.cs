using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #440 (split out of #402) — a session skipped for missing configuration is invisible.
///
/// <para>Both skip sites in <c>SessionIngestionService</c> logged a <c>[WRN]</c> and bumped a counter
/// that lands inside a run summary whose status is <b>Success</b>. On beta that ran for five days and
/// surfaced only because a Session Manager noticed a colleague's session had never appeared.</para>
///
/// <para>The failure is easy to miss in a specific way: the config check runs only on create, so every
/// session already in the table keeps updating normally. The app looks healthy and only <i>new</i>
/// sessions vanish.</para>
/// </summary>
public class SkippedSessionAlertTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 2, 5, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<SkippedSession> SeedSkipAsync(
        AppDbContext dbContext, Team team, string vecCode = "arrl",
        SkippedSessionReason reason = SkippedSessionReason.NoMatchingVec)
    {
        var skip = new SkippedSession
        {
            TeamId = team.Id,
            ExamToolsSessionId = "remote-1",
            VecCode = vecCode,
            Title = "W9NB Tacos and Testing Tuesday",
            ScheduledStartUtc = new DateTime(2026, 8, 19, 1, 0, 0, DateTimeKind.Utc),
            Reason = reason,
            FirstSeenUtc = Now,
            LastSeenUtc = Now
        };
        dbContext.SkippedSessions.Add(skip);
        await dbContext.SaveChangesAsync();
        return skip;
    }

    /// <summary>The whole point: it reaches the bell at all.</summary>
    [Fact]
    public async Task ASkippedSession_RaisesAnAlert()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        var item = Assert.Single(feed.Items);
        Assert.Equal(1, feed.TotalCount);
        Assert.Equal("HRCC", item.TeamName);
    }

    /// <summary>
    /// The alert has to name the fix, not the symptom. "5 sessions skipped" sends somebody hunting;
    /// the VEC code the feed actually sent is the string they type into Admin → VECs.
    /// </summary>
    [Fact]
    public async Task TheAlert_QuotesTheVecCodeTheFeedSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team, vecCode: "lagroup");

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Contains("lagroup", Assert.Single(feed.Items).Detail);
    }

    /// <summary>
    /// <c>OccurredUtc</c> is first-seen, not last-seen — "how long has this been broken" is the
    /// question that matters for a fault nothing else reports, and last-seen would reset every poll
    /// and make a five-day-old problem look like it started this morning.
    /// </summary>
    [Fact]
    public async Task TheAlert_ReportsWhenItStarted_NotWhenItWasLastSeen()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var skip = await SeedSkipAsync(dbContext, team);
        skip.LastSeenUtc = Now.AddDays(5);
        await dbContext.SaveChangesAsync();

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Equal(Now, Assert.Single(feed.Items).OccurredUtc);
    }

    /// <summary>The two reasons have different fixes, so they must not share a destination.</summary>
    [Theory]
    [InlineData(SkippedSessionReason.NoMatchingVec, "/Admin/Vecs")]
    [InlineData(SkippedSessionReason.NoFeeConfiguration, "/Admin/FeeConfigurations")]
    public async Task TheAlert_PointsAtThePageThatFixesIt(SkippedSessionReason reason, string expectedPage)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team, reason: reason);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.Equal(expectedPage, Assert.Single(feed.Items).PageName);
    }

    /// <summary>
    /// Mike's ruling, 2026-08-20: <b>if it is team-related, a TeamAdmin sees it.</b> Their team's
    /// sessions are the ones going missing, so being told is not optional — the first cut gated this
    /// to SystemAdmin because both fix pages are SystemAdmin-only, which protected the no-403 rule by
    /// withholding the information instead of routing it.
    /// </summary>
    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task ARoleWithNoAdminSurface_IsNotOfferedTheAlert(UserRole role)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(role, null, CancellationToken.None);

        Assert.Empty(feed.Items);
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    public async Task BothAdminRoles_AreOfferedTheAlert(UserRole role)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(role, [team.Id], CancellationToken.None);

        Assert.Single(feed.Items);
    }

    /// <summary>Team scoping, same semantics as every other source: a TeamAdmin sees their own teams only.</summary>
    [Fact]
    public async Task AnotherTeamsSkip_IsNotShown()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext);
        var theirs = await SeedTeamAsync(dbContext, "MARC");
        await SeedSkipAsync(dbContext, theirs);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, [mine.Id], CancellationToken.None);

        Assert.Empty(feed.Items);
    }
    /// <summary>
    /// The rule that lets both things be true at once: the alert goes to whoever needs to know, and
    /// still never links anywhere the reader cannot open. A SystemAdmin gets the page that fixes it;
    /// a TeamAdmin gets Job Run History, which is <c>RoleGroups.Admins</c> and is where the ingestion
    /// runs that skipped their sessions are listed.
    /// </summary>
    [Theory]
    [InlineData(UserRole.SystemAdmin, SkippedSessionReason.NoMatchingVec, "/Admin/Vecs")]
    [InlineData(UserRole.SystemAdmin, SkippedSessionReason.NoFeeConfiguration, "/Admin/FeeConfigurations")]
    [InlineData(UserRole.TeamAdmin, SkippedSessionReason.NoMatchingVec, "/Admin/JobRunHistory")]
    [InlineData(UserRole.TeamAdmin, SkippedSessionReason.NoFeeConfiguration, "/Admin/JobRunHistory")]
    public async Task TheDestination_IsAPageThatRoleCanActuallyOpen(UserRole role, SkippedSessionReason reason, string expectedPage)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team, reason: reason);

        var feed = await new AlertFeedService(dbContext).GetAsync(role, null, CancellationToken.None);

        Assert.Equal(expectedPage, Assert.Single(feed.Items).PageName);
    }

    /// <summary>
    /// A TeamAdmin cannot perform either fix, so the alert has to say who can — otherwise it reports a
    /// problem and leaves the reader with no next step, which is how an alert becomes noise.
    /// </summary>
    [Fact]
    public async Task ATeamAdmin_IsToldItNeedsASystemAdministrator()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.TeamAdmin, null, CancellationToken.None);

        Assert.Contains("system administrator", Assert.Single(feed.Items).Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a SystemAdmin is not told to go and find one — they are the one who fixes it.</summary>
    [Fact]
    public async Task ASystemAdmin_IsNotToldToAskSomebodyElse()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(UserRole.SystemAdmin, null, CancellationToken.None);

        Assert.DoesNotContain("system administrator", Assert.Single(feed.Items).Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whoever is reading, the VEC code the feed sent is the actionable fact and stays in the text.</summary>
    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    public async Task TheVecCode_IsQuotedForEitherAdmin(UserRole role)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team, vecCode: "lagroup");

        var feed = await new AlertFeedService(dbContext).GetAsync(role, null, CancellationToken.None);

        Assert.Contains("lagroup", Assert.Single(feed.Items).Detail);
    }
}
