using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Reporting;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The stats page's numbers (#63) — VE testing activity alongside applicant volume.
///
/// <para>Two things here are quietly easy to get wrong, and both have precedent in this codebase:
/// which sessions count as having happened (<c>Status == Active</c> means "not cancelled", never
/// "finished" — that misreading shipped twice), and which calendar month a session falls in (evening
/// Eastern sessions are stored on the following UTC date, so UTC month-grouping misfiles most of
/// them).</para>
/// </summary>
public class SessionStatsServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class Fixture
    {
        public required Team Team { get; init; }
        public required Vec Vec { get; init; }
        public required FeeConfiguration Fee { get; init; }
    }

    private static async Task<Fixture> SeedRefsAsync(AppDbContext dbContext, string teamName = "HRCC")
    {
        var team = new Team { Name = teamName, ExamToolsTeamCode = teamName };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var fee = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedByUser = user
        };
        dbContext.Teams.Add(team);
        dbContext.FeeConfigurations.Add(fee);
        await dbContext.SaveChangesAsync();
        return new Fixture { Team = team, Vec = vec, Fee = fee };
    }

    /// <summary>A finished session unless told otherwise — completion is what the whole report filters on.</summary>
    private static Session Session(Fixture f, DateTime startUtc, bool completed = true) => new()
    {
        ExamToolsSessionId = Guid.NewGuid().ToString(),
        Title = "Session",
        ScheduledStartUtc = startUtc,
        Team = f.Team,
        Vec = f.Vec,
        FeeConfiguration = f.Fee,
        Status = SessionStatus.Active,
        ExamToolsClosedUtc = completed ? startUtc.AddHours(3) : null
    };

    private static Candidate Candidate(
        Session session, bool tested, CandidateApplicationStatus status,
        LicenseClass? initial = null, LicenseClass? earned = null) => new()
    {
        Session = session,
        Name = "Candidate",
        DateRegisteredUtc = session.ScheduledStartUtc.AddDays(-7),
        Tested = tested,
        ApplicationStatus = status,
        InitialLicenseClass = initial,
        NewLicenseClass = earned
    };

    private static SessionStatsService Service(AppDbContext dbContext) => new(dbContext);

    /// <summary>
    /// A scheduled-but-not-yet-run session must not appear. <c>Status</c> stays Active for every
    /// session a team has ever run, so filtering on it would have counted next month's.
    /// </summary>
    [Fact]
    public async Task OnlyCompletedSessionsAreCounted()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        dbContext.Sessions.Add(Session(f, Now.AddDays(-30)));
        dbContext.Sessions.Add(Session(f, Now.AddDays(30), completed: false));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Equal(1, report.TotalSessions);
    }

    /// <summary>
    /// Passed is derived, not stored: tested, not Failed, and not withdrawn. The withdrawal exclusion
    /// is the one that matters — <c>NotTested</c> is the withdrawn state, and counting it as a pass
    /// would inflate every rate on the page.
    /// </summary>
    [Fact]
    public async Task PassedExcludesFailedAndWithdrawnCandidates()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        var session = Session(f, Now.AddDays(-10));
        dbContext.Sessions.Add(session);
        dbContext.Candidates.AddRange(
            Candidate(session, tested: true, CandidateApplicationStatus.Granted),
            Candidate(session, tested: true, CandidateApplicationStatus.Received),
            Candidate(session, tested: true, CandidateApplicationStatus.Failed),
            Candidate(session, tested: false, CandidateApplicationStatus.NotTested),
            Candidate(session, tested: false, CandidateApplicationStatus.Unmatched));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Equal(3, report.TotalCandidatesTested);
        Assert.Equal(2, report.TotalPassed);
        Assert.Equal(1, report.TotalFailed);
        Assert.Equal(2d / 3d, report.PassRate);
    }

    /// <summary>
    /// The pass rate is "of those whose result is known". A candidate still awaiting the FCC is
    /// neither — counting them as failures would make a session run last week report a rate that
    /// silently climbs for a fortnight afterwards.
    /// </summary>
    [Fact]
    public async Task PassRateIsNullWhenNoResultIsKnownYet()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        var session = Session(f, Now.AddDays(-1));
        dbContext.Sessions.Add(session);
        dbContext.Candidates.Add(Candidate(session, tested: false, CandidateApplicationStatus.Unmatched));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Null(report.PassRate);
    }

    /// <summary>
    /// Walking in with nothing is a first license; walking in with a class is an upgrade. Both need a
    /// class earned this sitting, since that is what "walked out with something" means.
    /// </summary>
    [Fact]
    public async Task NewLicensesAndUpgradesAreSplitByWhatTheyWalkedInWith()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        var session = Session(f, Now.AddDays(-10));
        dbContext.Sessions.Add(session);
        dbContext.Candidates.AddRange(
            Candidate(session, true, CandidateApplicationStatus.Granted, initial: LicenseClass.None, earned: LicenseClass.Technician),
            Candidate(session, true, CandidateApplicationStatus.Granted, initial: null, earned: LicenseClass.Technician),
            Candidate(session, true, CandidateApplicationStatus.Granted, initial: LicenseClass.Technician, earned: LicenseClass.General),
            Candidate(session, true, CandidateApplicationStatus.Failed, initial: LicenseClass.Technician, earned: null));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, report.TotalNewLicenses);
        Assert.Equal(1, report.TotalUpgrades);
    }

    /// <summary>
    /// <b>Months are Eastern months.</b> A session at 01:00 UTC on 1 March is 20:00 ET on 28
    /// February — and evening ET is simply when volunteer-run sessions happen, so grouping on the UTC
    /// month misfiles a large share of them. This is the same class of bug as #248.
    /// </summary>
    [Fact]
    public async Task MonthsAreGroupedInEasternTime_NotUtc()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);

        // 01:00 UTC on 1 March 2026 = 20:00 ET on 28 February.
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 3, 1, 1, 0, 0, DateTimeKind.Utc)));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        var period = Assert.Single(report.Periods);
        Assert.Equal(2, period.MonthUtc.Month);
        Assert.Equal(2026, period.MonthUtc.Year);
    }

    [Fact]
    public async Task PeriodsAreOrderedOldestFirst_AndSplitByMonth()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 6, 10, 17, 0, 0, DateTimeKind.Utc)));
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 4, 10, 17, 0, 0, DateTimeKind.Utc)));
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 4, 20, 17, 0, 0, DateTimeKind.Utc)));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, report.Periods.Count);
        Assert.Equal(4, report.Periods[0].MonthUtc.Month);
        Assert.Equal(2, report.Periods[0].Sessions);
        Assert.Equal(6, report.Periods[1].MonthUtc.Month);
    }

    /// <summary>Team scoping, the convention every read here follows: null means every team merged.</summary>
    [Fact]
    public async Task TeamScopingNarrowsToOneTeam_AndNullMeansAll()
    {
        await using var dbContext = CreateContext();
        var a = await SeedRefsAsync(dbContext, "HRCC");
        var b = await SeedRefsAsync(dbContext, "MARC");
        dbContext.Sessions.Add(Session(a, Now.AddDays(-10)));
        dbContext.Sessions.Add(Session(b, Now.AddDays(-10)));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, (await Service(dbContext).GetAsync(null, null, null, CancellationToken.None)).TotalSessions);
        Assert.Equal(1, (await Service(dbContext).GetAsync([a.Team.Id], null, null, CancellationToken.None)).TotalSessions);
    }

    /// <summary>
    /// The VE half of the page. Counted from roster links on completed sessions and scoped the same
    /// way, so a VE's number always agrees with the range the rest of the page is showing.
    /// </summary>
    [Fact]
    public async Task VeActivityCountsSessionsWorked_MostActiveFirst()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        var first = Session(f, Now.AddDays(-20));
        var second = Session(f, Now.AddDays(-10));
        var future = Session(f, Now.AddDays(20), completed: false);
        dbContext.Sessions.AddRange(first, second, future);

        var busy = new VolunteerExaminer { Name = "Busy VE", CallSign = "K0AAA" };
        var quiet = new VolunteerExaminer { Name = "Quiet VE", CallSign = "K0BBB" };
        dbContext.VolunteerExaminers.AddRange(busy, quiet);
        await dbContext.SaveChangesAsync();

        dbContext.SessionVolunteerExaminers.AddRange(
            new SessionVolunteerExaminer { Session = first, VolunteerExaminer = busy },
            new SessionVolunteerExaminer { Session = second, VolunteerExaminer = busy },
            new SessionVolunteerExaminer { Session = second, VolunteerExaminer = quiet },
            // A future session must not count towards anybody's total.
            new SessionVolunteerExaminer { Session = future, VolunteerExaminer = quiet });
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, report.ActiveVolunteerExaminers);
        Assert.Equal("Busy VE", report.VolunteerExaminers[0].Name);
        Assert.Equal(2, report.VolunteerExaminers[0].SessionsWorked);
        Assert.Equal(1, report.VolunteerExaminers[1].SessionsWorked);
    }

    [Fact]
    public async Task TheDateRangeBoundsBothEnds()
    {
        await using var dbContext = CreateContext();
        var f = await SeedRefsAsync(dbContext);
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc)));
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 5, 15, 17, 0, 0, DateTimeKind.Utc)));
        dbContext.Sessions.Add(Session(f, new DateTime(2026, 9, 15, 17, 0, 0, DateTimeKind.Utc)));
        await dbContext.SaveChangesAsync();

        var report = await Service(dbContext).GetAsync(
            null,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(1, report.TotalSessions);
    }
}
