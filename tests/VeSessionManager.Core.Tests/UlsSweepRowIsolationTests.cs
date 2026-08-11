using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The three ULS sweeps each loop over a batch they loaded tracked, and each had a different half of
/// the same bug (issues #231, #247, #249). What they share is the failure <i>signature</i>: the run
/// reports a healthy count, Job History renders green, and nothing was written.
///
/// <list type="bullet">
///   <item><b>#231</b> — <c>VolunteerExaminerLicenseWatchService</c> caught per row and called
///   <c>ChangeTracker.Clear()</c>, which detaches <i>everything</i>, including the rest of the batch.
///   One bad row on VE #7 of 250 left #8-250 being mutated while detached: saves wrote nothing and
///   <c>Checked++</c> still ran.</item>
///   <item><b>#249</b> — <c>LicenseWatchService</c> had no catch at all, on a loop whose own comment
///   says a vanity rename colliding with the unique index <i>will</i> throw. One collision abandoned
///   every remaining licence in the run.</item>
///   <item><b>#247</b> — <c>UlsWatcherService</c> never stamped an attempt, so with the new cap a
///   permanently-failing row would sort first (null leads) and starve everyone behind it forever.
///   </item>
/// </list>
///
/// <para>All three fixes are "isolate the row that failed, keep the rest" — so they are tested
/// together, by the property that actually matters: <b>work done before and after a failure is
/// persisted, and the counters do not lie about it.</b></para>
/// </summary>
public class UlsSweepRowIsolationTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    // The repo's own fixture rather than Microsoft.Extensions.TimeProvider.Testing — no new
    // package, matching LicenseWatchServiceTests and friends.
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Throws for one nominated call sign and answers normally for the rest.</summary>
    private sealed class ThrowingForOneClient(string throwsFor, UlsLookupResult ok) : IUlsLookupClient
    {
        public List<string> Seen { get; } = [];

        public Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken)
        {
            Seen.Add(frn);
            if (frn == throwsFor) throw new InvalidOperationException($"boom for {frn}");
            return Task.FromResult<UlsLookupResult?>(ok);
        }
    }

    /// <summary>Always fails to reach the mirror — the "learned nothing" case, not an exception.</summary>
    private sealed class AlwaysNullClient : IUlsLookupClient
    {
        /// <summary>In call order — the ordering test asserts on this, not on a count.</summary>
        public List<string> Seen { get; } = [];

        public int Calls => Seen.Count;

        public Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken)
        {
            Seen.Add(frn);
            return Task.FromResult<UlsLookupResult?>(null);
        }
    }

    private static UlsLookupResult Found(string callSign) => new()
    {
        Found = true,
        LicenseStatus = "Active",
        CallSign = callSign,
        OperatorClass = LicenseClass.Technician,
        GrantDateUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ExpiredDateUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    // ---- #231: the VE sweep must not detach the whole batch -------------------------------------

    [Fact]
    public async Task VeSweep_OneRowThrowing_StillPersistsEveryOtherRow()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "T", ExamToolsTeamCode = "T" };

        // Three VEs. The middle one throws; the ones on either side must still be written.
        foreach (var callSign in new[] { "AA1AA", "BB2BB", "CC3CC" })
        {
            var ve = new VolunteerExaminer { Name = callSign, CallSign = callSign, CreatedUtc = Now.AddYears(-1) };
            ve.TeamMemberships.Add(new VeTeamMembership { Team = team, IsActive = true, CreatedUtc = Now.AddYears(-1) });
            dbContext.VolunteerExaminers.Add(ve);
        }
        await dbContext.SaveChangesAsync();

        var client = new ThrowingForOneClient("BB2BB", Found("AA1AA"));
        var service = new VolunteerExaminerLicenseWatchService(
            dbContext, client, new FixedTimeProvider(Now), NullLogger<VolunteerExaminerLicenseWatchService>.Instance);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Failures);

        // The property that was broken: the stamp actually reached the database for the rows that
        // succeeded. Under ChangeTracker.Clear() these stayed null while Checked still counted them,
        // so the next run repeated the whole sweep and the failure was invisible.
        var persisted = await dbContext.VolunteerExaminers.AsNoTracking().ToListAsync();
        var stamped = persisted.Where(v => v.LicenseLastCheckedUtc is not null).Select(v => v.Name).Order().ToList();

        Assert.Equal(["AA1AA", "CC3CC"], stamped);
        Assert.Equal(result.Checked, stamped.Count);
        Assert.Null(persisted.Single(v => v.Name == "BB2BB").LicenseLastCheckedUtc);
    }

    // ---- #249: the watch list must survive a row that throws on save -----------------------------

    [Fact]
    public async Task WatchList_OneRowThrowing_StillChecksAndPersistsTheRest()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "T", ExamToolsTeamCode = "T" };
        dbContext.Teams.Add(team);
        foreach (var callSign in new[] { "AA1AA", "BB2BB", "CC3CC" })
        {
            dbContext.WatchedLicenses.Add(new WatchedLicense
            {
                Team = team,
                CallSign = callSign,
                AddedUtc = Now.AddYears(-1)
            });
        }
        await dbContext.SaveChangesAsync();

        var client = new ThrowingForOneClient("BB2BB", Found("ZZ9ZZ"));
        var service = new LicenseWatchService(
            dbContext, client, new FixedTimeProvider(Now), NullLogger<LicenseWatchService>.Instance);

        var result = await service.RunAsync(CancellationToken.None);

        // Before the fix this threw straight out of RunAsync: the job died, the two innocent rows
        // were never looked at, and JobRunHistory recorded a failed run with no detail.
        Assert.Equal(1, result.Failures);
        Assert.Equal(2, result.Checked);
        Assert.Equal(3, client.Seen.Count);

        var persisted = await dbContext.WatchedLicenses.AsNoTracking().ToListAsync();
        Assert.Equal(2, persisted.Count(w => w.LastCheckedUtc is not null));
        Assert.Null(persisted.Single(w => w.CallSign == "BB2BB").LastCheckedUtc);
    }

    // ---- #247: a failed lookup must still take its turn ------------------------------------------

    [Fact]
    public async Task UlsWatcher_LookupFailure_StillStampsTheAttempt()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "T", ExamToolsTeamCode = "T" };
        var session = new Session
        {
            Team = team,
            ScheduledStartUtc = Now.AddDays(-1),
            ExamToolsSessionId = "s1",
            Title = "S"
        };
        dbContext.Candidates.Add(new Candidate
        {
            Session = session,
            Name = "C",
            Frn = "0038704029",
            Tested = true,
            ApplicationStatus = CandidateApplicationStatus.Unmatched,
            InitialLicenseClass = LicenseClass.None,
            NewLicenseClass = LicenseClass.Technician
        });
        await dbContext.SaveChangesAsync();

        var client = new AlwaysNullClient();
        var service = new UlsWatcherService(
            dbContext, client, new FixedTimeProvider(Now), NullLogger<UlsWatcherService>.Instance);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.LookupFailures);

        // The stamp is the whole point: without it this row keeps a null UlsLastCheckedUtc, nulls
        // sort first, and once the cap binds it leads the queue on every future run — the starvation
        // the sibling sweep already suffers from with its unstamped skips.
        var candidate = await dbContext.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(Now, candidate.UlsLastCheckedUtc);
        Assert.Equal(CandidateApplicationStatus.Unmatched, candidate.ApplicationStatus);
    }

    [Fact]
    public async Task UlsWatcher_OrdersByLeastRecentlyChecked_SoTheCapIsFairRatherThanACliff()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "T", ExamToolsTeamCode = "T" };
        var session = new Session
        {
            Team = team,
            ScheduledStartUtc = Now.AddDays(-1),
            ExamToolsSessionId = "s1",
            Title = "S"
        };

        // Checked longest ago, never checked, checked recently — deliberately not in that order.
        var stamps = new (string Frn, DateTime? Checked)[]
        {
            ("003recent", Now.AddHours(-1)),
            ("003oldest", Now.AddDays(-9)),
            ("003never", null)
        };
        foreach (var (frn, checkedUtc) in stamps)
        {
            dbContext.Candidates.Add(new Candidate
            {
                Session = session,
                Name = frn,
                Frn = frn,
                Tested = true,
                ApplicationStatus = CandidateApplicationStatus.Unmatched,
                InitialLicenseClass = LicenseClass.None,
                NewLicenseClass = LicenseClass.Technician,
                UlsLastCheckedUtc = checkedUtc
            });
        }
        await dbContext.SaveChangesAsync();

        var client = new AlwaysNullClient();
        await new UlsWatcherService(dbContext, client, new FixedTimeProvider(Now),
            NullLogger<UlsWatcherService>.Instance).RunAsync(CancellationToken.None);

        // Ordering, not the cap itself, is what makes the cap safe: never-checked first (null leads),
        // then longest-ago, then most recent. A cap without this ordering is a permanent cliff for
        // whoever happens to sort last — so this asserts the actual call ORDER, not that three calls
        // happened.
        Assert.Equal(["003never", "003oldest", "003recent"], client.Seen);
    }
}
