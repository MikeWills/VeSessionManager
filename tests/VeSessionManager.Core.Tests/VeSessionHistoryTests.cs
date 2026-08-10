using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The sessions-worked block on a VE's detail page (requested 2026-08-10): total, this year, split
/// by team, plus the most recent few.
///
/// <para>Two things here are easy to get wrong and invisible when you do — counting sessions that
/// have not happened, and putting a New Year's Eve session in the wrong year.</para>
/// </summary>
public class VeSessionHistoryTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, string callSign)
    {
        var person = new VolunteerExaminer { Name = "Test VE", CallSign = callSign };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    /// <param name="finished">A session only counts as worked when it actually happened. Status is NOT that signal — it only ever means "not cancelled".</param>
    private static async Task SeedWorkedSessionAsync(
        AppDbContext dbContext, Team team, VolunteerExaminer person, DateTime startUtc, bool finished = true, string title = "Session")
    {
        var session = new Session
        {
            TeamId = team.Id,
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = title,
            ScheduledStartUtc = startUtc,
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = finished ? startUtc.AddHours(3) : null
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = person.Id });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// The trap this codebase has now hit three times. <c>Status == Active</c> means "not cancelled",
    /// never "finished" — so a session the VE is merely booked for must not appear in either count.
    /// </summary>
    [Fact]
    public async Task AFutureBookingIsNotCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = await SeedVeAsync(dbContext, "N2SPG");
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await SeedWorkedSessionAsync(dbContext, team, person, now.AddDays(-30));
        await SeedWorkedSessionAsync(dbContext, team, person, now.AddDays(30), finished: false);

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, now, 5, CancellationToken.None);

        Assert.Equal(1, history.Total);
        Assert.Single(history.Recent);
    }

    /// <summary>
    /// Sessions run in the evening, so a session at 00:30 UTC on January 1st was the previous
    /// December 31st to everyone who was at it — and the page renders every date in ET. Counting on
    /// the raw UTC year puts it in the wrong one.
    /// </summary>
    [Fact]
    public async Task AnEveningSessionOnNewYearsEveCountsInTheYearItWasHeld()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = await SeedVeAsync(dbContext, "N2SPG");

        // 2026-01-01 00:30 UTC == 2025-12-31 19:30 ET. Last year's session, by any human account.
        await SeedWorkedSessionAsync(dbContext, team, person, new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc));
        // 2026-01-01 18:00 UTC == 2026-01-01 13:00 ET. This year's.
        await SeedWorkedSessionAsync(dbContext, team, person, new DateTime(2026, 1, 1, 18, 0, 0, DateTimeKind.Utc));

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, now, 5, CancellationToken.None);

        Assert.Equal(2, history.Total);
        Assert.Equal(1, history.ThisYear);
        Assert.Equal(2026, history.Year);
    }

    [Fact]
    public async Task CountsAreSplitByTeamAndTheTotalsAgree()
    {
        await using var dbContext = CreateContext();
        var hrcc = await SeedTeamAsync(dbContext, "HRCC");
        var marc = await SeedTeamAsync(dbContext, "MARC");
        var person = await SeedVeAsync(dbContext, "N2SPG");
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await SeedWorkedSessionAsync(dbContext, hrcc, person, now.AddDays(-10));
        await SeedWorkedSessionAsync(dbContext, hrcc, person, now.AddDays(-20));
        await SeedWorkedSessionAsync(dbContext, hrcc, person, new DateTime(2025, 6, 1, 18, 0, 0, DateTimeKind.Utc));
        await SeedWorkedSessionAsync(dbContext, marc, person, now.AddDays(-5));

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, now, 5, CancellationToken.None);

        Assert.Equal(4, history.Total);
        Assert.Equal(3, history.ThisYear);
        Assert.Equal(history.Total, history.ByTeam.Sum(t => t.Total));

        var hrccRow = history.ByTeam.Single(t => t.TeamName == "HRCC");
        Assert.Equal(3, hrccRow.Total);
        Assert.Equal(2, hrccRow.ThisYear);
        Assert.Equal(1, history.ByTeam.Single(t => t.TeamName == "MARC").Total);
    }

    /// <summary>A TeamAdmin sharing a VE with another team has no business reading that team's session titles off this page.</summary>
    [Fact]
    public async Task TeamScopeExcludesOtherTeamsSessions()
    {
        await using var dbContext = CreateContext();
        var hrcc = await SeedTeamAsync(dbContext, "HRCC");
        var marc = await SeedTeamAsync(dbContext, "MARC");
        var person = await SeedVeAsync(dbContext, "N2SPG");
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await SeedWorkedSessionAsync(dbContext, hrcc, person, now.AddDays(-10), title: "HRCC session");
        await SeedWorkedSessionAsync(dbContext, marc, person, now.AddDays(-5), title: "MARC session");

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, [hrcc.Id], now, 5, CancellationToken.None);

        Assert.Equal(1, history.Total);
        Assert.Equal("HRCC session", Assert.Single(history.Recent).Title);
    }

    [Fact]
    public async Task RecentIsMostRecentFirstAndCapped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = await SeedVeAsync(dbContext, "N2SPG");
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 1; i <= 8; i++)
        {
            await SeedWorkedSessionAsync(dbContext, team, person, now.AddDays(-i), title: $"Session {i}");
        }

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, now, 5, CancellationToken.None);

        Assert.Equal(8, history.Total);
        Assert.Equal(5, history.Recent.Count);
        Assert.Equal("Session 1", history.Recent[0].Title);
        Assert.Equal("Session 5", history.Recent[4].Title);
    }

    [Fact]
    public async Task ACancelledSessionIsNotCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = await SeedVeAsync(dbContext, "N2SPG");
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await SeedWorkedSessionAsync(dbContext, team, person, now.AddDays(-10));
        var cancelled = await dbContext.Sessions.FirstAsync();
        cancelled.Status = SessionStatus.Cancelled;
        await dbContext.SaveChangesAsync();

        var history = await new VolunteerExaminerReportService(dbContext)
            .GetPersonSessionHistoryAsync(person.Id, null, now, 5, CancellationToken.None);

        Assert.Equal(0, history.Total);
    }
}
