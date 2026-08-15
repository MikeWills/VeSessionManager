using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Reconciliation;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The nightly ExamTools-vs-database sweep (built 2026-08-10) — see docs/reconciliation.md.
///
/// <para><b>These tests can only prove the diff, never the premise.</b> The bug this job exists for
/// — the import dropping every month's final day — had a full green suite, because the fakes shared
/// our own wrong assumption about the date bound. So what is asserted here is the bookkeeping:
/// that a discrepancy is noticed once, stays one row while it persists, resolves when it goes away,
/// and comes back if it returns. Whether ExamTools agrees with us about anything is a question only
/// the live feed can answer, which is the entire reason this runs as a job rather than a test.</para>
/// </summary>
public class ReconciliationServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public List<ExamToolsSession> ClosedSessions { get; } = [];
        public List<(DateOnly Start, DateOnly End)> WindowsRequested { get; } = [];

        public void Add(string id, DateTime date, int? applicantCount = null) =>
            ClosedSessions.Add(new ExamToolsSession { Id = id, Date = date, ApplicantCount = applicantCount });

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(
            ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateInclusiveUtc, CancellationToken cancellationToken)
        {
            WindowsRequested.Add((startDateUtc, endDateInclusiveUtc));
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>(ClosedSessions);
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct) =>
            throw new NotSupportedException("Not used by ReconciliationService.");
        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            throw new NotSupportedException("Not used by ReconciliationService.");
        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            throw new NotSupportedException("Not used by ReconciliationService.");
        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) =>
            throw new NotSupportedException("Not used by ReconciliationService.");
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReconciliationService CreateService(AppDbContext dbContext, FakeExamToolsClient client, FixedTimeProvider time) =>
        new(dbContext, client, Options.Create(new ExamToolsOptions()), time, NullLogger<ReconciliationService>.Instance);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool configured = true)
    {
        var team = new Team
        {
            Name = "HRCC",
            ExamToolsTeamCode = configured ? "HRCC" : null,
            ExamToolsUsername = configured ? "user" : null,
            ExamToolsPassword = configured ? "pass" : null
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task SeedLocalSessionAsync(AppDbContext dbContext, Team team, string examToolsId, DateTime startUtc, int candidates = 0)
    {
        var session = new Session
        {
            TeamId = team.Id,
            ExamToolsSessionId = examToolsId,
            Title = "Session",
            ScheduledStartUtc = startUtc,
            Status = SessionStatus.Active
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        for (var i = 0; i < candidates; i++)
        {
            dbContext.Candidates.Add(new Candidate { SessionId = session.Id, Name = $"C{i}", DateRegisteredUtc = startUtc });
        }
        await dbContext.SaveChangesAsync();
    }

    // ---- the two windows agree (#280) ----

    /// <summary>
    /// The remote feed is asked for whole days; the local query must use the same boundary.
    ///
    /// <para>It used to be <c>now - Window</c>, carrying the run's time-of-day, while the remote
    /// start is midnight-aligned — so a session near the far edge could be <b>returned by ExamTools
    /// and excluded from the local set</b>, producing a MissingSession finding for a session we
    /// plainly have.</para>
    ///
    /// <para>The job's cadence is IntervalFromWorkerStart, so its run time-of-day is arbitrary; this
    /// test pins a run at midday and a session at 02:00 on the very first day of the window, which is
    /// the combination that used to fail. It never self-corrected, either — by the next night the
    /// session had aged out of both windows and RecordAsync stops re-examining findings that leave
    /// the window.</para>
    /// </summary>
    [Fact]
    public async Task ASessionOnTheFirstDayOfTheWindowIsNotReportedMissing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        // Midnight-aligned start of the remote window, then early morning on that same day — inside
        // the day ExamTools was asked about, but earlier than the run's own time-of-day.
        var windowStartDate = DateOnly.FromDateTime(Now - ReconciliationService.Window);
        var edgeSessionUtc = windowStartDate.ToDateTime(new TimeOnly(2, 0), DateTimeKind.Utc);

        var client = new FakeExamToolsClient();
        client.Add("et-edge", edgeSessionUtc);
        await SeedLocalSessionAsync(dbContext, team, "et-edge", edgeSessionUtc);

        var time = new FixedTimeProvider(Now);
        var result = await CreateService(dbContext, client, time).RunAsync(team, CancellationToken.None);

        // We have it, so it is not missing — and the local count must have seen it at all.
        Assert.Equal(1, result.LocalSessions);
        Assert.Empty(await dbContext.ReconciliationFindings.ToListAsync());
    }

    // ---- the finding the whole feature exists for ----

    [Fact]
    public async Task ASessionExamToolsHasAndWeDoNotIsRecorded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-present", Now.AddDays(-10));
        client.Add("et-missing", Now.AddDays(-5));
        await SeedLocalSessionAsync(dbContext, team, "et-present", Now.AddDays(-10));

        var result = await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.MissingSessions);
        var finding = await dbContext.ReconciliationFindings.SingleAsync();
        Assert.Equal(ReconciliationFindingKind.MissingSession, finding.Kind);
        Assert.Equal("et-missing", finding.ExamToolsSessionId);
        Assert.Null(finding.ResolvedUtc);
    }

    /// <summary>Ten nights of the same problem is one row with a moving LastSeenUtc, or the list grows without bound and its size stops meaning anything.</summary>
    [Fact]
    public async Task TheSameDiscrepancySeenAgainRefreshesTheRowRatherThanAddingOne()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-missing", Now.AddDays(-5));
        var time = new FixedTimeProvider(Now);
        var service = CreateService(dbContext, client, time);

        await service.RunAsync(team, CancellationToken.None);
        time.UtcNow = Now.AddDays(1);
        await service.RunAsync(team, CancellationToken.None);

        var finding = await dbContext.ReconciliationFindings.SingleAsync();
        Assert.Equal(Now, finding.FirstSeenUtc);
        Assert.Equal(Now.AddDays(1), finding.LastSeenUtc);
    }

    [Fact]
    public async Task AFindingResolvesOnceTheSessionIsImported()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-missing", Now.AddDays(-5));
        var time = new FixedTimeProvider(Now);
        var service = CreateService(dbContext, client, time);
        await service.RunAsync(team, CancellationToken.None);

        // What a re-import would do.
        await SeedLocalSessionAsync(dbContext, team, "et-missing", Now.AddDays(-5));
        time.UtcNow = Now.AddDays(1);
        var result = await service.RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.MissingSessions);
        var finding = await dbContext.ReconciliationFindings.SingleAsync();
        // Stamped, not deleted: "this was wrong and is now fixed" is worth keeping.
        Assert.Equal(Now.AddDays(1), finding.ResolvedUtc);
    }

    /// <summary>A discrepancy that comes back is the same standing fact again, not a second row — the unique index would refuse one anyway.</summary>
    [Fact]
    public async Task AResolvedFindingReopensIfTheProblemReturns()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-missing", Now.AddDays(-5));
        var time = new FixedTimeProvider(Now);
        var service = CreateService(dbContext, client, time);

        await service.RunAsync(team, CancellationToken.None);
        await SeedLocalSessionAsync(dbContext, team, "et-missing", Now.AddDays(-5));
        time.UtcNow = Now.AddDays(1);
        await service.RunAsync(team, CancellationToken.None);

        // The local row goes away again (a purge, a bad migration — the cause doesn't matter).
        dbContext.Sessions.RemoveRange(dbContext.Sessions);
        await dbContext.SaveChangesAsync();
        time.UtcNow = Now.AddDays(2);
        await service.RunAsync(team, CancellationToken.None);

        var finding = await dbContext.ReconciliationFindings.SingleAsync();
        Assert.Null(finding.ResolvedUtc);
        Assert.Equal(Now.AddDays(2), finding.LastSeenUtc);
    }

    // ---- candidate counts ----

    [Fact]
    public async Task ExamToolsHavingMoreApplicantsThanWeHoldIsRecorded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-1", Now.AddDays(-5), applicantCount: 12);
        await SeedLocalSessionAsync(dbContext, team, "et-1", Now.AddDays(-5), candidates: 9);

        var result = await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidateMismatches);
        Assert.Contains("12", (await dbContext.ReconciliationFindings.SingleAsync()).Detail);
    }

    /// <summary>
    /// Fewer at ExamTools than here is normal, not a fault: a withdrawn candidate is removed there
    /// and deliberately kept here. Flagging it would fill the page with noise that is working as
    /// designed, and a page full of noise gets ignored.
    /// </summary>
    [Fact]
    public async Task FewerApplicantsAtExamToolsIsNotAFinding()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-1", Now.AddDays(-5), applicantCount: 4);
        await SeedLocalSessionAsync(dbContext, team, "et-1", Now.AddDays(-5), candidates: 7);

        var result = await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidateMismatches);
        Assert.Empty(dbContext.ReconciliationFindings);
    }

    // ---- boundaries ----

    /// <summary>
    /// A finding for a session that has simply aged out of the window must NOT be marked resolved.
    /// Nothing was fixed — we stopped looking — and silently clearing it would be the most
    /// misleading thing this job could do.
    /// </summary>
    [Fact]
    public async Task AFindingOlderThanTheWindowIsLeftAloneRatherThanResolved()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var old = Now - ReconciliationService.Window - TimeSpan.FromDays(30);
        dbContext.ReconciliationFindings.Add(new ReconciliationFinding
        {
            TeamId = team.Id,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "et-ancient",
            SessionDateUtc = old,
            Detail = "older than the window",
            FirstSeenUtc = old,
            LastSeenUtc = old
        });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeExamToolsClient(), new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Null((await dbContext.ReconciliationFindings.SingleAsync()).ResolvedUtc);
    }

    [Fact]
    public async Task TheRequestedWindowIsInclusiveAndEndsToday()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        var (start, end) = Assert.Single(client.WindowsRequested);
        Assert.Equal(DateOnly.FromDateTime(Now), end);
        Assert.Equal(DateOnly.FromDateTime(Now - ReconciliationService.Window), start);
    }

    /// <summary>An unconfigured team is skipped quietly — the optional-integration rule. It must not produce a finding claiming ExamTools has nothing.</summary>
    [Fact]
    public async Task AnUnconfiguredTeamIsSkippedWithoutCallingExamTools()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, configured: false);
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.WindowsRequested);
        Assert.Equal(0, result.MissingSessions);
        Assert.Empty(dbContext.ReconciliationFindings);
    }

    /// <summary>The summary is what reaches JobRunHistory.ResultSummary — "the job ran" and "the job found three problems" must not look the same on the dashboard.</summary>
    [Fact]
    public async Task TheResultSummarySaysWhatWasFound()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Add("et-missing", Now.AddDays(-5));

        var result = await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        Assert.Contains("missing 1", result.ToString());
    }

    // ---- findings that age out of the window (2026-08-15) ----
    //
    // Reported live: four MissingSession findings for April 2026 sessions sat Open with a
    // "Re-import Apr 2026" button that appeared to do nothing however many times it was pressed.
    // The import was working. The sweep was not: RecordAsync loaded only findings with
    // SessionDateUtc inside the 120-day window, so once a finding's session aged past it, it was
    // never examined again and could never be resolved. It stayed on the page and in the nav badge
    // permanently. The mechanism was already described in this service's own comment about #280,
    // as a consequence of a different bug.

    /// <summary>
    /// A MissingSession finding whose session has since been imported must resolve <b>even after it
    /// has aged out of the remote window</b>. Verifying it needs no ExamTools call at all: the
    /// finding's claim is "this app never ingested session X", and whether X is in the database now
    /// is a local question.
    /// </summary>
    [Fact]
    public async Task AnAgedOutMissingSessionFinding_ResolvesOnceTheSessionHasBeenImported()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        // 200 days ago — comfortably outside the 120-day window, like the April findings were.
        var agedOut = Now.AddDays(-200);
        dbContext.ReconciliationFindings.Add(new ReconciliationFinding
        {
            TeamId = team.Id,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "old-session",
            SessionDateUtc = agedOut,
            Detail = "ExamTools has a closed session that this app never ingested.",
            FirstSeenUtc = agedOut.AddDays(1),
            LastSeenUtc = agedOut.AddDays(1)
        });
        await dbContext.SaveChangesAsync();

        // The re-import has since brought it in.
        await SeedLocalSessionAsync(dbContext, team, "old-session", agedOut);

        // The remote feed covers only the last 120 days and knows nothing about it.
        await CreateService(dbContext, new FakeExamToolsClient(), new FixedTimeProvider(Now))
            .RunAsync(team, CancellationToken.None);

        var finding = dbContext.ReconciliationFindings.Single();
        Assert.NotNull(finding.ResolvedUtc);
    }

    /// <summary>
    /// The other half, and the one that stops the fix becoming "resolve everything old": an aged-out
    /// finding whose session is still genuinely absent stays Open. Absence from the remote feed means
    /// "not looked at" out here, never "fixed".
    /// </summary>
    [Fact]
    public async Task AnAgedOutMissingSessionFinding_StaysOpenWhileTheSessionIsStillAbsent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var agedOut = Now.AddDays(-200);
        dbContext.ReconciliationFindings.Add(new ReconciliationFinding
        {
            TeamId = team.Id,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "still-missing",
            SessionDateUtc = agedOut,
            Detail = "ExamTools has a closed session that this app never ingested.",
            FirstSeenUtc = agedOut.AddDays(1),
            LastSeenUtc = agedOut.AddDays(1)
        });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeExamToolsClient(), new FixedTimeProvider(Now))
            .RunAsync(team, CancellationToken.None);

        Assert.Null(dbContext.ReconciliationFindings.Single().ResolvedUtc);
    }

    /// <summary>
    /// An aged-out finding belonging to another team must not be touched, however tempting a
    /// team-wide "resolve what is fixed" sweep looks — the id is ExamTools' and is only unique within
    /// a team's feed.
    /// </summary>
    [Fact]
    public async Task AnAgedOutFinding_IsNotResolvedByAnotherTeamsImport()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext);
        var teamB = new Team { Name = "MARC", ExamToolsTeamCode = "MARC", ExamToolsUsername = "u", ExamToolsPassword = "p" };
        dbContext.Teams.Add(teamB);
        await dbContext.SaveChangesAsync();

        var agedOut = Now.AddDays(-200);
        dbContext.ReconciliationFindings.Add(new ReconciliationFinding
        {
            TeamId = teamB.Id,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "shared-id",
            SessionDateUtc = agedOut,
            Detail = "ExamTools has a closed session that this app never ingested.",
            FirstSeenUtc = agedOut,
            LastSeenUtc = agedOut
        });
        await dbContext.SaveChangesAsync();

        // Team A imports a session that happens to carry the same ExamTools id.
        await SeedLocalSessionAsync(dbContext, teamA, "shared-id", agedOut);

        await CreateService(dbContext, new FakeExamToolsClient(), new FixedTimeProvider(Now))
            .RunAsync(teamA, CancellationToken.None);

        Assert.Null(dbContext.ReconciliationFindings.Single().ResolvedUtc);
    }

    /// <summary>
    /// The detail sentence quotes a calendar date, and it must be the <b>Eastern</b> one — the same
    /// date the page's own Session Date column renders, and the date a VE would recognise. ExamTools'
    /// <c>Date</c> is a UTC instant, and 697 of 867 sessions here start between 23:00 and 04:00 UTC,
    /// so formatting it raw names tomorrow for most of them. Live symptom: a card headed "Apr 15,
    /// 2026 ET" whose own text said "a closed session on 2026-04-16".
    /// </summary>
    [Fact]
    public async Task MissingSessionDetail_QuotesTheEasternDate_NotTheUtcOne()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // 01:00 UTC on the 16th is 21:00 ET on the 15th — an ordinary evening session.
        client.Add("evening", new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc));

        await CreateService(dbContext, client, new FixedTimeProvider(Now)).RunAsync(team, CancellationToken.None);

        var finding = dbContext.ReconciliationFindings.Single();
        Assert.Contains("2026-07-15", finding.Detail);
        Assert.DoesNotContain("2026-07-16", finding.Detail);
    }
}
