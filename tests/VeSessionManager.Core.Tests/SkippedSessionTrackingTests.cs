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
    /// ⚠️ <b>SystemAdmin only, including not TeamAdmin</b> — a narrower gate than reconciliation's.
    /// Both destinations (Admin → VECs, Admin → Fee Configurations) carry
    /// <c>RoleGroups.SystemAdminOnly</c>, so anyone else offered this alert gets a 403.
    /// <c>AlertPageRoleGateTests</c> caught this when the source first shipped gated admin-wide.
    ///
    /// <para>The cost is deliberate: a TeamAdmin whose team's sessions are silently vanishing does not
    /// see this. Both fixes genuinely belong to a SystemAdmin, and a link the reader cannot open is
    /// worse than no link.</para>
    /// </summary>
    [Theory]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task ARoleThatCannotOpenTheFixPage_IsNotOfferedTheAlert(UserRole role)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSkipAsync(dbContext, team);

        var feed = await new AlertFeedService(dbContext).GetAsync(role, null, CancellationToken.None);

        Assert.Empty(feed.Items);
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    public async Task ARoleThatCanFixIt_IsOfferedTheAlert(UserRole role)
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
}
