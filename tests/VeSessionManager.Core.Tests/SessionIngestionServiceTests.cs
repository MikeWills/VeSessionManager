using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SessionIngestionServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionStart = new(2026, 7, 24, 17, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>Stores sessions/applicants per TeamId so multi-team tests can prove one team's poll never sees another's data — mirrors the real API, which is scoped by the credentials.TeamCode passed in.</summary>
    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public Dictionary<int, List<ExamToolsSession>> SessionsByTeam { get; } = [];
        public Dictionary<int, List<ExamToolsSession>> ClosedSessionsByTeam { get; } = [];
        public Dictionary<int, Dictionary<string, List<ExamToolsApplicant>>> ApplicantsByTeam { get; } = [];
        public List<string> ApplicantFetches { get; } = [];
        public List<ExamToolsCredentials> CredentialsUsed { get; } = [];

        /// <summary>Session ids whose applicant fetch should throw, standing in for an ExamTools error on one session.</summary>
        public HashSet<string> ThrowOnApplicantFetchFor { get; } = [];

        public List<ExamToolsSession> SessionsFor(int teamId) =>
            SessionsByTeam.TryGetValue(teamId, out var list) ? list : SessionsByTeam[teamId] = [];

        // Mirrors the real, separate GET .../sessions/{start}/{end}?group=all&team=... feed — a
        // "done" session put here (not in SessionsFor) is only visible via GetTeamClosedSessionsAsync,
        // exactly like the real API. See docs/examtools-api.md's "Closed sessions are a separate feed".
        public List<ExamToolsSession> ClosedSessionsFor(int teamId) =>
            ClosedSessionsByTeam.TryGetValue(teamId, out var list) ? list : ClosedSessionsByTeam[teamId] = [];

        public Dictionary<string, List<ExamToolsApplicant>> ApplicantsFor(int teamId) =>
            ApplicantsByTeam.TryGetValue(teamId, out var dict) ? dict : ApplicantsByTeam[teamId] = [];

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>(SessionsFor(credentials.TeamId));
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsSession>>(ClosedSessionsFor(credentials.TeamId));

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
        {
            ApplicantFetches.Add(examToolsSessionId);
            if (ThrowOnApplicantFetchFor.Contains(examToolsSessionId))
            {
                throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found).");
            }

            var applicants = ApplicantsFor(credentials.TeamId);
            return Task.FromResult<IReadOnlyList<ExamToolsApplicant>>(
                applicants.TryGetValue(examToolsSessionId, out var list) ? list : []);
        }

        // Not exercised by these tests (Phase 7's VE roster sync has its own test file/fake) —
        // implemented only to satisfy the interface.
        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);

        // Not exercised by these tests (ExamResultSyncService has its own test file/fake) —
        // implemented only to satisfy the interface.
        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
            Task.FromResult<ExamToolsApplicantDetail?>(null);
    }

    /// <summary>
    /// Pass a databaseName to open a <b>second</b> context over the same store. That is the only way
    /// these tests can see a load actually happen: every other test here seeds and runs through one
    /// context, so the rows are already in the change tracker and EF's relationship fixup populates
    /// Session.Candidates whether the service queried for them or not. See the two
    /// LoadedFromTheDatabase tests, and #246 for what that hid.
    /// </summary>
    private static AppDbContext CreateContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Seeds a fully-configured Team (IsExamToolsConfigured = true by default).</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string teamCode = "TESTTEAM")
    {
        var team = new Team
        {
            Name = teamCode,
            ExamToolsTeamCode = teamCode,
            ExamToolsUsername = "testuser",
            ExamToolsPassword = "testpass",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Seeds the Vec/User/FeeConfiguration rows ingestion depends on (mirrors DevDataSeeder). Vec is shared/global, not Team-scoped — see docs/multi-team.md.</summary>
    private static async Task SeedVecAndFeeConfigAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            RetainedAmount = 7m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
    }

    private static SessionIngestionService CreateService(AppDbContext dbContext, FakeExamToolsClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), Options.Create(new ExamToolsOptions()), NullLogger<SessionIngestionService>.Instance);

    private static ExamToolsSession PendingSession(
        string id = "session-1", DateTime? date = null, int? applicantCount = 0, string summary = "July Session",
        string? extId = "AD2GX", string? teamLeadCallsign = "KM6Z") =>
        new()
        {
            Id = id,
            Date = date ?? SessionStart,
            Vec = "arrl",
            State = "pend",
            ApplicantCount = applicantCount,
            SessionDef = new ExamToolsSessionDef { Summary = summary, ExtId = extId, TeamLeadCallsign = teamLeadCallsign }
        };

    private static ExamToolsApplicant Applicant(
        string id = "applicant-1", string first = "Roana", string last = "Glory",
        string email = "roana@example.com", string frn = "0012345678") =>
        new()
        {
            Id = id,
            Firstname = first,
            Lastname = last,
            Email = email,
            Frn = frn,
            HasFelony = false,
            Created = new DateTime(2026, 7, 10, 2, 28, 2, DateTimeKind.Utc)
        };

    [Fact]
    public async Task NewPendingSession_IsInserted_AndRepollDoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        var sut = CreateService(dbContext, client);

        var result = await sut.RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("session-1", session.ExamToolsSessionId);
        Assert.Equal("July Session", session.Title);
        Assert.Equal("AD2GX", session.ExtId);
        Assert.Equal(SessionStart, session.ScheduledStartUtc);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(Now, session.CreatedUtc);
        Assert.NotEqual(0, session.VecId);
        Assert.Equal(team.Id, session.TeamId);
        Assert.NotEqual(0, session.FeeConfigurationId);

        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task StalePendingSessionInThePast_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Observed on the real dev feed: sessions from years ago still in state "pend".
        client.SessionsFor(team.Id).Add(PendingSession(id: "stale-session", date: Now.AddYears(-2)));

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task DoneSessionWithinBackfillWindow_IsIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Issue #22: teams want to backfill sessions that already completed — a "done" session is
        // ingestable for the first time (previously never ingested regardless of date). The window
        // narrowed from 30 days to 7 in issue #67, so this date moved inside the new bound; pulling
        // real history is HistoricalImportService's job now, not this sweep's.
        var done = PendingSession(id: "completed-session", date: Now.AddDays(-3));
        done.State = "done";
        client.SessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("completed-session", session.ExamToolsSessionId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.TestingCompletedUtc); // deliberately not pre-marked — lets candidate sync run normally
    }

    [Fact]
    public async Task DoneSessionOlderThanBackfillWindow_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // The feed returns unfiltered full history — a "done" session from years ago is exactly as
        // undesirable to backfill as a zombie "pend" one (see StalePendingSessionInThePast_IsNotIngested).
        var done = PendingSession(id: "ancient-session", date: Now.AddYears(-2));
        done.State = "done";
        client.SessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task DoneSessionOnlyInClosedSessionsFeed_IsIngested()
    {
        // Confirmed live 2026-07-28 against real HRCC ExamTools data: GetTeamSessionsAsync's own
        // feed never returns a "done" session, no matter its age — closed sessions only ever show
        // up via the separate GetTeamClosedSessionsAsync feed. This is the merge SessionIngestionService
        // must perform for issue #22's backfill to actually work against real data.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "closed-feed-session", date: Now.AddDays(-1));
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("closed-feed-session", session.ExamToolsSessionId);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task SessionInBothPendAndClosedFeeds_IsNotIngestedTwice()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(id: "dual-feed-session", date: Now.AddDays(1)));
        var done = PendingSession(id: "dual-feed-session", date: Now.AddDays(1));
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task SettledClosedSession_IsSkippedFromTheClosedFeedOnEveryLaterTick()
    {
        // Issue #67: once a session is stored locally AND we have observed ExamTools close it,
        // re-reading it out of the closed feed does nothing useful — the only remaining effects are
        // ApplyRescheduleRules (meaningless for a session that already ran) and the long-complete
        // ExtId backfill. Both are asserted absent here, which is what proves the skip happened.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();

        // Tick 1: ingested while open, with no ExtId yet (the pre-2026-07-30 state the backfill
        // exists for). Tick 2: observed closing, which sets ExamToolsClosedUtc.
        client.SessionsFor(team.Id).Add(PendingSession(extId: null));
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        client.SessionsFor(team.Id).Clear();
        var done = PendingSession(extId: null);
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.NotNull(dbContext.Sessions.Single().ExamToolsClosedUtc);

        // Tick 3: the feed now reports a different date and an ExtId. A settled session ignores both.
        done.Date = done.Date.AddDays(4);
        done.SessionDef!.ExtId = "SHOULD-NOT-BACKFILL";
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsRescheduled);
        Assert.Equal(0, result.SessionsFlaggedForReview);
        var session = dbContext.Sessions.Single();
        Assert.False(session.RescheduleFlaggedForReview);
        Assert.Null(session.ExtId);
        // Still not cancelled — dropping it from remoteIds must not read as "disappeared".
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(0, result.SessionsCancelled);
    }

    [Fact]
    public async Task KnownButNotYetClosedSession_IsStillReadFromTheClosedFeed()
    {
        // The guard rail on the test above: the skip keys on ExamToolsClosedUtc, NOT on "is this id
        // already stored locally". A known session that has never been seen closed still needs this
        // feed to receive its closed stamp at all — and without that stamp, issue #68's false
        // cancellations come straight back. Simplifying IsSettledLocally to an id check fails here.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Null(dbContext.Sessions.Single().ExamToolsClosedUtc);

        client.SessionsFor(team.Id).Clear();
        var done = PendingSession();
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsClosedByExamTools);
        Assert.Equal(Now, dbContext.Sessions.Single().ExamToolsClosedUtc);
        Assert.Equal(0, result.SessionsCancelled);
    }

    // ---- Historical import: VEC submission ----

    private const int ImportingUserId = 1;

    /// <summary>
    /// A historical session's VEC paperwork was filed months ago, outside this app — importing a
    /// range must not drop it into the submission tracker as outstanding work.
    /// </summary>
    [Fact]
    public async Task HistoricalImport_MarksCreatedSessionsSubmittedToVec()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-1");
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).ImportHistoricalRangeAsync(
            team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), ImportingUserId, CancellationToken.None);

        Assert.Equal(1, result.SessionsMarkedVecSubmitted);
        var session = dbContext.Sessions.Single();
        Assert.Equal(VecSubmissionStatus.Submitted, session.VecSubmissionStatus);
        Assert.Equal(ImportingUserId, session.VecSubmittedByUserId);
        Assert.Equal(Now, session.VecSubmittedDate);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "VecSubmissionMarked" && a.EntityId == session.Id);
    }

    /// <summary>
    /// The backlog case: a range imported before this behaviour existed is already stored, and an
    /// import skips sessions it already has — so the marking must happen outside the create branch,
    /// or re-running the range would fix nothing.
    /// </summary>
    [Fact]
    public async Task HistoricalImport_MarksAlreadyImportedSessions_OnARerun()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-1");
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var range = (Start: new DateOnly(2026, 1, 1), End: new DateOnly(2026, 1, 31));
        await CreateService(dbContext, client).ImportHistoricalRangeAsync(team, range.Start, range.End, ImportingUserId, CancellationToken.None);

        // Simulate the pre-fix state: the session exists but was never marked.
        var stored = dbContext.Sessions.Single();
        stored.VecSubmissionStatus = VecSubmissionStatus.NotSubmitted;
        stored.VecSubmittedDate = null;
        stored.VecSubmittedByUserId = null;
        await dbContext.SaveChangesAsync();

        var rerun = await CreateService(dbContext, client).ImportHistoricalRangeAsync(
            team, range.Start, range.End, ImportingUserId, CancellationToken.None);

        Assert.Equal(0, rerun.SessionsAdded); // nothing re-created
        Assert.Equal(1, rerun.SessionsMarkedVecSubmitted);
        Assert.Equal(VecSubmissionStatus.Submitted, dbContext.Sessions.Single().VecSubmissionStatus);
    }

    /// <summary>
    /// Mirrors VecSubmissionService's rule: an already-Submitted session keeps its original date and
    /// the user who recorded it. A re-run must never reassign credit for a real submission.
    /// </summary>
    [Fact]
    public async Task HistoricalImport_LeavesAnAlreadySubmittedSessionUntouched()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-1");
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var range = (Start: new DateOnly(2026, 1, 1), End: new DateOnly(2026, 1, 31));
        await CreateService(dbContext, client).ImportHistoricalRangeAsync(team, range.Start, range.End, ImportingUserId, CancellationToken.None);

        var originalDate = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc);
        var stored = dbContext.Sessions.Single();
        stored.VecSubmittedDate = originalDate;
        stored.VecSubmittedByUserId = 99; // a real Session Manager recorded this
        await dbContext.SaveChangesAsync();

        var rerun = await CreateService(dbContext, client).ImportHistoricalRangeAsync(
            team, range.Start, range.End, ImportingUserId, CancellationToken.None);

        Assert.Equal(0, rerun.SessionsMarkedVecSubmitted);
        var after = dbContext.Sessions.Single();
        Assert.Equal(originalDate, after.VecSubmittedDate);
        Assert.Equal(99, after.VecSubmittedByUserId);
    }

    /// <summary>
    /// Historical candidates are assumed granted so UlsWatcherService stops polling FCC about
    /// licenses from one to four years ago — one HTTP call per candidate, twice a day, forever.
    /// </summary>
    [Fact]
    public async Task HistoricalImport_AssumesCandidatesAreGranted_WithoutInventingLicenseData()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-1", applicantCount: 1);
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        client.ApplicantsFor(team.Id)["old-1"] = [Applicant()];

        var result = await CreateService(dbContext, client).ImportHistoricalRangeAsync(
            team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), ImportingUserId, CancellationToken.None);

        Assert.Equal(1, result.CandidatesAssumedGranted);
        var candidate = dbContext.Candidates.Single();
        Assert.Equal(CandidateApplicationStatus.Granted, candidate.ApplicationStatus);
        // Status only — a call sign or grant date here would be fabricated, never verified.
        Assert.Null(candidate.CallSign);
        Assert.Null(candidate.LicenseGrantDateUtc);
    }

    /// <summary>The routine poll must NOT pre-mark anything — only the historical import may assume this.</summary>
    [Fact]
    public async Task RoutineIngestion_DoesNotAssumeCandidatesAreGranted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var pending = PendingSession(applicantCount: 1);
        client.SessionsFor(team.Id).Add(pending);
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesAssumedGranted);
        Assert.Equal(CandidateApplicationStatus.Unmatched, dbContext.Candidates.Single().ApplicationStatus);
    }

    /// <summary>The routine poll must NOT pre-mark anything — only the historical import may assume this.</summary>
    [Fact]
    public async Task RoutineIngestion_DoesNotMarkSessionsSubmittedToVec()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsMarkedVecSubmitted);
        Assert.Equal(VecSubmissionStatus.NotSubmitted, dbContext.Sessions.Single().VecSubmissionStatus);
    }

    /// <summary>
    /// GLAARG's ExamTools code is "lagroup", not its name — a VEC row named for the org must still
    /// match, or every one of its sessions is skipped forever with only a log warning to show for it.
    /// </summary>
    [Fact]
    public async Task NewSession_VecMatchedByExamToolsCode_NotName_IsIngested()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var vec = new Vec { Name = "GLAARG", ExamToolsCode = "lagroup" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeExamToolsClient();
        var remote = PendingSession();
        remote.Vec = "lagroup";
        client.SessionsFor(team.Id).Add(remote);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        Assert.Equal(0, result.SessionsSkippedNoConfig);
        Assert.Equal(vec.Id, dbContext.Sessions.Single().VecId);
    }

    /// <summary>A VEC whose code is null still matches on its name — the pre-ExamToolsCode behaviour.</summary>
    [Fact]
    public async Task NewSession_VecWithNoExamToolsCode_StillMatchesOnName()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVecAndFeeConfigAsync(dbContext); // "ARRL", ExamToolsCode null
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession()); // reports "arrl"

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        Assert.Equal(0, result.SessionsSkippedNoConfig);
    }

    [Fact]
    public async Task NewSession_WithoutFeeConfiguration_IsSkippedAndIngestsOnceConfigExists()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Vec exists but has no fee configuration yet.
        dbContext.Vecs.Add(new Vec { Name = "ARRL" });
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSkippedNoConfig);
        Assert.Empty(dbContext.Sessions);

        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            VecId = dbContext.Vecs.Single().Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var retryResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(1, retryResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task NewApplicants_AreInsertedWithMappedFields()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] =
        [
            Applicant(),
            Applicant(id: "applicant-2", first: "Tomasina", last: "Susanna", email: "tomasina@example.com", frn: "0000000000")
        ];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(2, result.CandidatesAdded);
        var candidates = dbContext.Candidates.OrderBy(c => c.ExamToolsApplicantId).ToList();

        Assert.Equal("Roana Glory", candidates[0].Name);
        Assert.Equal("roana@example.com", candidates[0].Email);
        Assert.Equal("0012345678", candidates[0].Frn);
        Assert.False(candidates[0].FrnMissingAtRegistration);
        Assert.False(candidates[0].HasFelonyDisclosure);
        Assert.Equal(new DateTime(2026, 7, 10, 2, 28, 2, DateTimeKind.Utc), candidates[0].DateRegisteredUtc);
        Assert.Equal(CandidateApplicationStatus.Unmatched, candidates[0].ApplicationStatus);

        // ExamTools' all-zeros FRN placeholder means "registered without an FRN".
        Assert.Null(candidates[1].Frn);
        Assert.True(candidates[1].FrnMissingAtRegistration);
    }

    [Fact]
    public async Task Repoll_DoesNotDuplicateCandidates_AndAppliesChangedEmail()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(email: "new-address@example.com")];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesAdded);
        Assert.Equal(1, result.CandidatesUpdated);
        var candidate = Assert.Single(dbContext.Candidates);
        Assert.Equal("new-address@example.com", candidate.Email);
    }

    [Fact]
    public async Task PurgedOrTerminalCandidates_AreNotUpdated()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(), Applicant(id: "applicant-2", email: "second@example.com")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var purged = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1");
        purged.Name = null;
        purged.Email = null;
        purged.PiiPurgedUtc = Now;
        var terminal = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2");
        terminal.ApplicationStatus = CandidateApplicationStatus.Granted;
        await dbContext.SaveChangesAsync();

        client.ApplicantsFor(team.Id)["session-1"] =
        [
            Applicant(email: "resurrected@example.com"),
            Applicant(id: "applicant-2", email: "changed@example.com")
        ];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesUpdated);
        Assert.Null(dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1").Email);
        Assert.Equal("second@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2").Email);
    }

    [Fact]
    public async Task FrnPlaceholderInFeed_DoesNotOverwriteManuallyEnteredFrn()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(frn: "0000000000")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // Session Manager fills the FRN in manually later (spec allows testing without one initially).
        dbContext.Candidates.Single().Frn = "0099999999";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal("0099999999", dbContext.Candidates.Single().Frn);
    }

    [Fact]
    public async Task ExistingSessionWithNullExtId_BackfillsFromNextPoll_ButNeverOverwritesOnceSet()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(extId: null)); // simulates a session ingested before ExtId existed
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Null(dbContext.Sessions.Single().ExtId);

        client.SessionsFor(team.Id)[0] = PendingSession(extId: "AD2GX");
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal("AD2GX", dbContext.Sessions.Single().ExtId);

        // A later poll reporting a different ExtId (e.g. lead VE reassigned) must not overwrite —
        // same "set once" precedent as Title itself.
        client.SessionsFor(team.Id)[0] = PendingSession(extId: "W5CBW");
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal("AD2GX", dbContext.Sessions.Single().ExtId);
    }

    [Fact]
    public async Task Reschedule_WithNoBlockingCandidates_IsAppliedAutomatically()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsRescheduled);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal(newStart, session.ScheduledStartUtc);
        Assert.False(session.RescheduleFlaggedForReview);
    }

    [Fact]
    public async Task Reschedule_WithOnlyTerminalCandidates_IsAppliedAutomatically()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // A withdrawn/no-show candidate is terminal and should not block an automatic reschedule.
        dbContext.Candidates.Single().ApplicationStatus = CandidateApplicationStatus.NotTested;
        await dbContext.SaveChangesAsync();

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsRescheduled);
        Assert.Equal(newStart, dbContext.Sessions.Single().ScheduledStartUtc);
    }

    [Fact]
    public async Task Reschedule_WithRegisteredCandidates_FlagsOnceAndKeepsStoredTime()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsFlaggedForReview);
        Assert.Equal(0, result.SessionsRescheduled);
        var session = Assert.Single(dbContext.Sessions.ToList());
        Assert.Equal(SessionStart, session.ScheduledStartUtc); // stored time untouched
        Assert.True(session.RescheduleFlaggedForReview);
        Assert.Equal(Now, session.RescheduleFlaggedUtc);
        var audit = Assert.Single(dbContext.AuditLogs);
        Assert.Null(audit.UserId);
        Assert.Equal("RescheduleFlaggedForReview", audit.Action);
        Assert.Equal(nameof(Session), audit.EntityType);

        // Same mismatch on the next poll must not re-flag or add another audit row.
        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsFlaggedForReview);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task SessionMissingFromFeed_IsMarkedCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.SessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsCancelled);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal(Now, session.CancelledUtc);

        // A second poll must not "re-cancel" (CancelledUtc stays put) or count it again.
        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsCancelled);
    }

    [Fact]
    public async Task CompletedSessionMissingFromFeed_IsNotCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var session = dbContext.Sessions.Single();
        session.TestingCompletedUtc = Now;
        await dbContext.SaveChangesAsync();

        client.SessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(SessionStatus.Active, dbContext.Sessions.Single().Status);
    }

    [Fact]
    public async Task InProgressSession_IsIngested_AndItsCandidatesSync()
    {
        // ExamTools' "go" state renders as "In progress" on its own session list (confirmed on the
        // dev site 2026-07-31). Mike: "it can be in go status and we get a new candidate" — so a
        // session first seen mid-session must still be ingestable, or that candidate is invisible
        // for the whole session. Previously "go" fell through ShouldIngestNewSession's unknown-state
        // case and the session was never created at all.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        var inProgress = PendingSession(id: "live-session", date: Now.AddMinutes(-20), applicantCount: 1);
        inProgress.State = "go";
        client.SessionsFor(team.Id).Add(inProgress);
        client.ApplicantsFor(team.Id)["live-session"] = [Applicant(id: "walked-in")];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        Assert.Equal(1, result.CandidatesAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("live-session", session.ExamToolsSessionId);
        Assert.Equal(SessionStatus.Active, session.Status);
        // "go" is not "done" — an in-progress session must not be stamped closed.
        Assert.Null(session.ExamToolsClosedUtc);

        // A candidate registering after the session has started is picked up on the next poll.
        client.ApplicantsFor(team.Id)["live-session"] = [Applicant(id: "walked-in"), Applicant(id: "late-arrival")];
        client.SessionsFor(team.Id)[0].ApplicantCount = 2;
        var second = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(1, second.CandidatesAdded);
        Assert.Equal(2, dbContext.Candidates.Count());
    }

    [Fact]
    public async Task StaleInProgressSession_IsNotIngested()
    {
        // The dev feed carries a session stuck "In progress" since 2024 — as undesirable to ingest
        // as any other zombie, so "go" gets the same NewSessionPastGrace bound as "pend".
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        var stale = PendingSession(id: "stuck-in-progress", date: Now.AddYears(-2));
        stale.State = "go";
        client.SessionsFor(team.Id).Add(stale);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task OneSessionFailingCandidateSync_DoesNotStopTheOtherSessions()
    {
        // Found live 2026-07-31: a single session ExamTools answered with 404 threw out of the
        // candidate loop, so HRCC's whole pipeline — candidates, VE roster, payments, emails —
        // stopped for two hours, every tick, with a failed JobRunHistory row as the only symptom.
        // A session the API can't serve must be skipped, not fatal.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(id: "broken-session", applicantCount: 1));
        client.SessionsFor(team.Id).Add(PendingSession(id: "healthy-session", applicantCount: 1));
        client.ApplicantsFor(team.Id)["broken-session"] = [Applicant(id: "a")];
        client.ApplicantsFor(team.Id)["healthy-session"] = [Applicant(id: "b")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.Candidates.Count());

        // ExamTools starts failing on one session only, and a real change lands on the other.
        client.ThrowOnApplicantFetchFor.Add("broken-session");
        client.ApplicantsFor(team.Id)["healthy-session"] = [Applicant(id: "b", email: "moved@example.com")];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // The run completes rather than throwing, and reports the failure instead of hiding it.
        Assert.Equal(1, result.SessionsFailedCandidateSync);
        Assert.Equal(1, result.CandidatesUpdated);
        Assert.Equal("moved@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "b").Email);

        // The broken session's candidate is left exactly as it was — not withdrawn, not cleared.
        var stranded = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "a");
        Assert.NotEqual(CandidateApplicationStatus.NotTested, stranded.ApplicationStatus);
        Assert.NotNull(stranded.Name);
    }

    [Fact]
    public async Task ApplicantGoneFromFeed_IsMarkedWithdrawn_WithPiiClearedAndPaymentsKept()
    {
        // Issue #70: a candidate who cancels simply stops appearing in the applicant export. Lands
        // in the same state CandidateActionService.DeleteAsync produces, so the UI/PII purge/reporting
        // can't tell the manual and automatic routes apart.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] =
            [Applicant(id: "stays"), Applicant(id: "cancels", first: "Dana", last: "Vale", email: "dana@example.com", frn: "0087654321")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.Candidates.Count());

        // Give the leaver a payment, to prove money is left alone.
        var leaver = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "cancels");
        dbContext.Payments.Add(new Payment
        {
            CandidateId = leaver.Id,
            Amount = 15m,
            Status = PaymentStatus.Unpaid,
            Reason = PaymentReason.InitialExam,
            PaymentLinkUrl = "https://squareup.com/pay/abc",
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        client.SessionsFor(team.Id)[0].ApplicantCount = 1;
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "stays")];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesWithdrawn);
        var withdrawn = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "cancels");
        Assert.Equal(CandidateApplicationStatus.NotTested, withdrawn.ApplicationStatus);
        Assert.Null(withdrawn.Name);
        Assert.Null(withdrawn.Email);
        Assert.NotNull(withdrawn.PiiPurgedUtc);
        Assert.Null(withdrawn.ResultMarkedByUserId); // no human made this call

        // The payment row survives — only the live checkout link is nulled with the rest of the PII.
        var payment = dbContext.Payments.Single(p => p.CandidateId == withdrawn.Id);
        Assert.Equal(15m, payment.Amount);
        Assert.Equal(PaymentStatus.Unpaid, payment.Status);
        Assert.Null(payment.PaymentLinkUrl);

        // The candidate who stayed is untouched.
        var kept = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "stays");
        Assert.NotEqual(CandidateApplicationStatus.NotTested, kept.ApplicationStatus);
        Assert.NotNull(kept.Name);

        // Idempotent: already NotTested, so a second poll withdraws nobody again.
        var repoll = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repoll.CandidatesWithdrawn);
    }

    /// <summary>
    /// #246 split RunAsync's one team-wide `.Include(s => s.Candidates).ThenInclude(c => c.Payments)`
    /// into a scalar session query plus a second candidate query bounded to the sessions in this
    /// run's feed. Everything downstream still reads <c>Session.Candidates</c>, which now arrives by
    /// EF relationship fixup rather than by Include.
    ///
    /// <para><b>Nothing in the suite could see that.</b> Every other test here seeds and runs through
    /// a single context, so the candidates are already tracked and the collection is populated no
    /// matter what the service queries — the whole 1479-test suite passed with the candidate load
    /// deleted outright. The second context is the point of this test: it reproduces the Worker's
    /// real shape, a fresh scope per tick with an empty change tracker.</para>
    ///
    /// <para>If the load regresses, <c>local.Candidates</c> is empty, every applicant looks new, and
    /// ingestion silently re-adds the entire roster — duplicate candidates, and in turn duplicate
    /// payments and emails.</para>
    /// </summary>
    [Fact]
    public async Task ExistingCandidates_AreLoadedFromTheDatabase_NotReAddedAsNew()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = CreateContext(databaseName))
        {
            await SeedVecAndFeeConfigAsync(seed);
            var seedTeam = await SeedTeamAsync(seed);
            var seedClient = new FakeExamToolsClient();
            seedClient.SessionsFor(seedTeam.Id).Add(PendingSession(applicantCount: 1));
            seedClient.ApplicantsFor(seedTeam.Id)["session-1"] = [Applicant()];
            var seeded = await CreateService(seed, seedClient).RunAsync(seedTeam, CancellationToken.None);
            Assert.Equal(1, seeded.CandidatesAdded);
        }

        await using var dbContext = CreateContext(databaseName);
        var team = await dbContext.Teams.SingleAsync();
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesAdded);
        Assert.Equal(1, dbContext.Candidates.Count());
    }

    /// <summary>
    /// The Payments half of the same split. Payments are eager-loaded because
    /// CandidatePiiFields.Clear nulls a withdrawing candidate's live Square checkout link — with
    /// Payments unloaded it would clear the Candidate half and leave a payable link alive, which is
    /// money, and silent.
    ///
    /// <para>Second context for the same reason as the test above: the existing withdrawal test
    /// asserts this exact property and passes even with the candidate load removed entirely,
    /// because one context tracks everything.</para>
    /// </summary>
    [Fact]
    public async Task WithdrawalClearsTheCheckoutLink_WhenPaymentsWereNotAlreadyTracked()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = CreateContext(databaseName))
        {
            await SeedVecAndFeeConfigAsync(seed);
            var seedTeam = await SeedTeamAsync(seed);
            var seedClient = new FakeExamToolsClient();
            seedClient.SessionsFor(seedTeam.Id).Add(PendingSession(applicantCount: 2));
            seedClient.ApplicantsFor(seedTeam.Id)["session-1"] =
                [Applicant(id: "stays"), Applicant(id: "cancels", first: "Dana", last: "Vale", email: "dana@example.com", frn: "0087654321")];
            await CreateService(seed, seedClient).RunAsync(seedTeam, CancellationToken.None);

            var leaver = seed.Candidates.Single(c => c.ExamToolsApplicantId == "cancels");
            seed.Payments.Add(new Payment
            {
                CandidateId = leaver.Id,
                Amount = 15m,
                Status = PaymentStatus.Unpaid,
                Reason = PaymentReason.InitialExam,
                PaymentLinkUrl = "https://squareup.com/pay/abc",
                CreatedUtc = Now
            });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = CreateContext(databaseName);
        var team = await dbContext.Teams.SingleAsync();
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "stays")];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesWithdrawn);
        var withdrawn = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "cancels");
        Assert.Null(withdrawn.Name);

        // The row survives; only the live checkout link is nulled. This is the assertion that needs
        // Payments to have come back with the candidate.
        var payment = dbContext.Payments.Single(p => p.CandidateId == withdrawn.Id);
        Assert.Equal(15m, payment.Amount);
        Assert.Null(payment.PaymentLinkUrl);
    }

    [Fact]
    public async Task ApplicantExportDisagreeingWithTheFeedCount_WithdrawsNobody()
    {
        // The guard that matters most. An empty or truncated-but-successful export must never be
        // read as "everyone withdrew" — that is the one way this feature could wipe a live roster,
        // and clearing PII is not reversible. Two independent fields (the session feed's own
        // applicantCount and the export itself) have to agree before anything is withdrawn.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "a"), Applicant(id: "b")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.Candidates.Count());

        // ExamTools still says 2, but the export comes back empty — a bad response, not a mass exodus.
        client.ApplicantsFor(team.Id)["session-1"] = [];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesWithdrawn);
        Assert.Equal(2, dbContext.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested));
        Assert.All(dbContext.Candidates.ToList(), c => Assert.NotNull(c.Name));
    }

    [Fact]
    public async Task TestedCandidateGoneFromFeed_IsNeverWithdrawn()
    {
        // Same refusal DeleteAsync makes: someone who actually sat the exam is not a withdrawal,
        // whatever the feed says afterwards.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "tested-already")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var candidate = dbContext.Candidates.Single();
        candidate.Tested = true;
        await dbContext.SaveChangesAsync();

        client.SessionsFor(team.Id)[0].ApplicantCount = 0;
        client.ApplicantsFor(team.Id)["session-1"] = [];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesWithdrawn);
        var untouched = dbContext.Candidates.Single();
        Assert.NotEqual(CandidateApplicationStatus.NotTested, untouched.ApplicationStatus);
        Assert.NotNull(untouched.Name);
    }

    [Fact]
    public async Task ExamToolsReportingSessionDone_RecordsExamToolsClosedUtc_ButNotTestingCompletedUtc()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Null(dbContext.Sessions.Single().ExamToolsClosedUtc);

        // ExamTools closes the session: it leaves the pend feed and appears in the closed feed.
        client.SessionsFor(team.Id).Clear();
        var done = PendingSession();
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsClosedByExamTools);
        var session = dbContext.Sessions.Single();
        Assert.Equal(Now, session.ExamToolsClosedUtc);
        // Load-bearing: ExamTools closing a session is an observation, not the Session Manager's
        // "Mark completed" decision, which carries side effects (candidates flipped to Tested,
        // Square orders completed, felony-disclosure emails). Conflating them would fire those.
        Assert.Null(session.TestingCompletedUtc);
        Assert.Equal(SessionStatus.Active, session.Status);

        // Never re-stamped on a later poll, so the timestamp keeps meaning "when we first saw it closed".
        var repoll = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repoll.SessionsClosedByExamTools);
        Assert.Equal(Now, dbContext.Sessions.Single().ExamToolsClosedUtc);
    }

    [Fact]
    public async Task ExamToolsClosedSession_AgingOutOfTheClosedFeed_IsNotCancelled()
    {
        // Regression for issue #68, found live 2026-07-31 against real HRCC data: two genuine
        // completed sessions (one with all three candidates already Tested) were flipped to
        // Cancelled exactly 30 days after they ran. Nothing ever moved a session out of "open" —
        // TestingCompletedUtc is only ever set by a human clicking Mark completed — so once a
        // session aged past CompletedSessionBackfillWindow it dropped out of the merged feed and
        // the "vanished from the feed == cancelled" heuristic fired on it.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.SessionsFor(team.Id).Clear();
        var done = PendingSession();
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // 30+ days later it falls out of the closed-session window too — the exact moment the bug fired.
        client.ClosedSessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        var session = dbContext.Sessions.Single();
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.CancelledUtc);
    }

    [Fact]
    public async Task AlreadyEndedSession_DisappearingFromFeed_IsNotCancelled_EvenWithoutAClosedStamp()
    {
        // The other half of issue #68. ExamToolsClosedUtc only protects sessions we actually
        // observed ExamTools close — sessions that had already aged out of the closed-session feed
        // before that field existed have no stamp and never will, so they were still due to be
        // flipped to Cancelled on the very next tick. Three real HRCC sessions were in exactly that
        // state when the fix was written. A session whose window has elapsed cannot meaningfully be
        // cancelled by vanishing, so HasEnded is the backstop that does not require observation.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Ingested via the closed feed (the only feed a completed session ever appears in), dated
        // inside the backfill window so it is created at all. A first ingest never stamps
        // ExamToolsClosedUtc — that only happens on a later poll of an already-known session — so
        // this lands in exactly the pre-fix state: already ended, no closed stamp, and never
        // going to get one now that it has aged out.
        var done = PendingSession(date: Now.AddDays(-3));
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var seeded = dbContext.Sessions.Single();
        Assert.Null(seeded.ExamToolsClosedUtc);
        Assert.True(seeded.HasEnded(Now));

        client.SessionsFor(team.Id).Clear();
        client.ClosedSessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        var session = dbContext.Sessions.Single();
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.CancelledUtc);
    }

    [Fact]
    public async Task ClosingRunStillSyncsCandidatesOnce_ThenSessionIsNeverPolledAgain()
    {
        // "Only poll it if it's open" includes the poll that discovers it closed — that run is the
        // last chance to pick up final candidate changes, so it must still sync. From the next run
        // on there is nothing further to pull: candidate-level updates for a finished session come
        // from ExamResultSyncService (per applicant id) and the ULS watcher (per FRN), neither of
        // which uses this feed.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Single(dbContext.Candidates);
        var fetchesWhileOpen = client.ApplicantFetches.Count;

        // ExamTools closes it, and a late change lands in that same feed response.
        client.SessionsFor(team.Id).Clear();
        var done = PendingSession(applicantCount: 1);
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(email: "changed@example.com")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // The closing run polled once more, and picked the change up.
        Assert.Equal(fetchesWhileOpen + 1, client.ApplicantFetches.Count);
        Assert.Equal("changed@example.com", dbContext.Candidates.Single().Email);

        // Every run after that leaves it alone.
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(fetchesWhileOpen + 1, client.ApplicantFetches.Count);
    }

    [Fact]
    public async Task ApplicantFetch_IsSkippedWhenFeedShowsZeroAndNoLocalCandidates()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 0));

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.ApplicantFetches);
    }

    // ---- Multi-team ----

    [Fact]
    public async Task TwoTeams_ShareTheGlobalVec_ButSessionsGetDistinctTeamId()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext); // one shared "ARRL" Vec/FeeConfiguration
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var client = new FakeExamToolsClient();
        client.SessionsFor(teamA.Id).Add(PendingSession(id: "teamA-session"));
        client.SessionsFor(teamB.Id).Add(PendingSession(id: "teamB-session"));

        await CreateService(dbContext, client).RunAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, client).RunAsync(teamB, CancellationToken.None);

        Assert.Equal(2, dbContext.Sessions.Count());
        var sessionA = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamA-session");
        var sessionB = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamB-session");
        Assert.Equal(teamA.Id, sessionA.TeamId);
        Assert.Equal(teamB.Id, sessionB.TeamId);
        // Both resolved to the same shared Vec — VECs are global, not per-team (see docs/multi-team.md).
        Assert.Equal(sessionA.VecId, sessionB.VecId);
        var vec = Assert.Single(dbContext.Vecs);
        Assert.Equal(vec.Id, sessionA.VecId);
    }

    [Fact]
    public async Task TeamBIngestion_NeverCancelsTeamAsStillActiveSessions()
    {
        // A naive "load every Session, diff against this team's feed" would wrongly see Team A's
        // session as "disappeared" from Team B's feed and cancel it. RunAsync scopes its local
        // session lookup to the team being ingested to prevent exactly this.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var client = new FakeExamToolsClient();
        client.SessionsFor(teamA.Id).Add(PendingSession(id: "teamA-session"));
        await CreateService(dbContext, client).RunAsync(teamA, CancellationToken.None);

        // Team B has never seen "teamA-session" and has no sessions of its own this poll.
        var result = await CreateService(dbContext, client).RunAsync(teamB, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(SessionStatus.Active, dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamA-session").Status);
    }

    [Fact]
    public async Task UnconfiguredTeam_SkipsIngestion_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "Unconfigured Team", CreatedUtc = Now }; // no ExamTools credentials set
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(client.CredentialsUsed);
        Assert.Empty(dbContext.Sessions);
    }

    // ---- RefreshSessionCandidatesAsync (session-scoped Detail-page refresh, 2026-08-03) ----

    /// <summary>
    /// The key behavioural guarantee versus RunAsync: a session-scoped refresh must never run
    /// cancellation detection or create sessions. Another local Active session that happens to be
    /// absent from the feed at that moment would be flipped to Cancelled by RunAsync's diff — a
    /// scoped refresh must leave it (and its candidates) completely alone.
    /// </summary>
    [Fact]
    public async Task RefreshSessionCandidates_SyncsOnlyThatSession_NeverCancelsOrCreatesOthers()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(id: "session-1", applicantCount: 1));
        client.SessionsFor(team.Id).Add(PendingSession(id: "session-2", applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "a1")];
        client.ApplicantsFor(team.Id)["session-2"] = [Applicant(id: "a2", email: "other@example.com")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.Sessions.Count());

        // The feed changes: session-1 gains an applicant and an email change, session-2 disappears
        // (RunAsync's cancellation signal), and a brand-new session-3 appears.
        client.SessionsFor(team.Id).Clear();
        client.SessionsFor(team.Id).Add(PendingSession(id: "session-1", applicantCount: 2));
        client.SessionsFor(team.Id).Add(PendingSession(id: "session-3"));
        client.ApplicantsFor(team.Id)["session-1"] =
            [Applicant(id: "a1", email: "changed@example.com"), Applicant(id: "a1-late")];
        client.ApplicantFetches.Clear();

        var target = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "session-1");
        var result = await CreateService(dbContext, client).RefreshSessionCandidatesAsync(team, target.Id, CancellationToken.None);

        // The named session's candidates are added/updated…
        Assert.Equal(1, result.CandidatesUpdated);
        Assert.Equal(1, result.CandidatesAdded);
        Assert.Equal("changed@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "a1").Email);
        Assert.Equal("session-1", Assert.Single(client.ApplicantFetches));

        // …and everything else is untouched: no new session, no cancellation, other roster intact.
        Assert.Equal(0, result.SessionsAdded);
        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(2, dbContext.Sessions.Count()); // session-3 was NOT created
        var other = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "session-2");
        Assert.Equal(SessionStatus.Active, other.Status);
        Assert.Null(other.CancelledUtc);
        Assert.Equal("other@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "a2").Email);
    }

    /// <summary>
    /// A session absent from the team feed (closed and aged out of the pend feed) has no
    /// applicantCount to cross-check the export against, so withdrawal detection must be skipped
    /// entirely — a local candidate missing from the export must NOT be withdrawn, because clearing
    /// PII is not reversible and absence-from-feed is not evidence of withdrawal.
    /// </summary>
    [Fact]
    public async Task RefreshSessionCandidates_SessionAbsentFromFeed_NeverWithdrawsAnybody()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "stays"), Applicant(id: "vanishes")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.Candidates.Count());

        // The session drops out of the feed entirely; the export now returns only one applicant.
        client.SessionsFor(team.Id).Clear();
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(id: "stays")];

        var target = dbContext.Sessions.Single();
        var result = await CreateService(dbContext, client).RefreshSessionCandidatesAsync(team, target.Id, CancellationToken.None);

        Assert.Equal(0, result.CandidatesWithdrawn);
        var missing = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "vanishes");
        Assert.NotEqual(CandidateApplicationStatus.NotTested, missing.ApplicationStatus);
        Assert.NotNull(missing.Name);
        Assert.Null(missing.PiiPurgedUtc);
    }

    /// <summary>
    /// A session already stamped ExamToolsClosedUtc before the refresh began has nothing left to
    /// give this feed — the PII-bearing applicant export must not be re-fetched for it (candidate
    /// updates for a finished session arrive via ExamResultSyncService / the ULS watcher instead).
    /// </summary>
    [Fact]
    public async Task RefreshSessionCandidates_SessionAlreadyClosed_DoesNotFetchApplicants()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var target = dbContext.Sessions.Single();
        target.ExamToolsClosedUtc = Now;
        await dbContext.SaveChangesAsync();
        client.ApplicantFetches.Clear();

        await CreateService(dbContext, client).RefreshSessionCandidatesAsync(team, target.Id, CancellationToken.None);

        Assert.Empty(client.ApplicantFetches);
    }

    /// <summary>
    /// Regression (reported live 2026-08-03): the session-scoped refresh read only the pend feed,
    /// which never carries a "done" session — so the close stamp was unreachable and this button
    /// could never close a session. It must fall back to the closed feed, and still perform the
    /// final candidate sync on the run that discovers the close (RunAsync's last-chance rule).
    /// </summary>
    [Fact]
    public async Task RefreshSessionCandidates_SessionClosedSincePendFeed_StampsClosedAndDoesFinalSync()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var target = dbContext.Sessions.Single();
        Assert.Null(target.ExamToolsClosedUtc);

        // ExamTools closes the session: it leaves the pend feed and appears in the closed feed only.
        client.SessionsFor(team.Id).Clear();
        var done = PendingSession(applicantCount: 2);
        done.State = "done";
        client.ClosedSessionsFor(team.Id).Add(done);
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(), Applicant(id: "applicant-2", first: "Dana", last: "Vale", email: "dana@example.com", frn: "0098765432")];
        client.ApplicantFetches.Clear();

        var result = await CreateService(dbContext, client).RefreshSessionCandidatesAsync(team, target.Id, CancellationToken.None);

        var refreshed = dbContext.Sessions.Single();
        Assert.NotNull(refreshed.ExamToolsClosedUtc);
        Assert.Equal(1, result.SessionsClosedByExamTools);
        // Last-chance rule: the run that discovers the close still syncs candidates.
        Assert.Single(client.ApplicantFetches);
        Assert.Equal(1, result.CandidatesAdded);
        Assert.Equal(SessionStatus.Active, refreshed.Status); // never cancelled by the scoped path
    }

    [Fact]
    public async Task RefreshSessionCandidates_UnconfiguredTeam_SkipsQuietly_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "Unconfigured Team", CreatedUtc = Now }; // no ExamTools credentials set
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client).RefreshSessionCandidatesAsync(team, 1, CancellationToken.None);

        Assert.Equal(0, result.CandidatesAdded);
        Assert.Empty(client.CredentialsUsed);
        Assert.Empty(client.ApplicantFetches);
    }

    // ---- Team lead (#223) ----

    /// <summary>
    /// ExamTools carries the lead's call sign on the team-list endpoint ingestion already polls, so
    /// this costs no extra request. It was arriving and being discarded before 2026-08-11.
    /// </summary>
    [Fact]
    public async Task NewSession_StoresTheTeamLeadCallSign()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: "km6z"));

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // Normalized on the way in, like every other call sign in this app.
        Assert.Equal("KM6Z", dbContext.Sessions.Single().TeamLeadCallSign);
    }

    /// <summary>
    /// Unlike Title and ExtId, which are set once, the lead is re-synced: it names a person who may
    /// be emailed about this session, so a stale value means notifying the wrong VE. Re-assigning
    /// also backfills sessions ingested before the field existed, with no migration script.
    /// </summary>
    [Fact]
    public async Task ExistingSession_TeamLeadIsKeptCurrentWhenItChangesUpstream()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var first = new FakeExamToolsClient();
        first.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: "KM6Z"));
        await CreateService(dbContext, first).RunAsync(team, CancellationToken.None);

        var second = new FakeExamToolsClient();
        second.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: "W9NB"));
        await CreateService(dbContext, second).RunAsync(team, CancellationToken.None);

        Assert.Equal("W9NB", dbContext.Sessions.Single().TeamLeadCallSign);
    }

    /// <summary>
    /// A feed that omits the lead must not erase one we already knew. "No value reported" and "the
    /// lead was removed" look identical here, and quietly forgetting is the worse of the two — it
    /// would silently drop whoever a notification was meant to reach.
    /// </summary>
    [Fact]
    public async Task ExistingSession_AbsentTeamLeadDoesNotEraseTheStoredOne()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);

        var first = new FakeExamToolsClient();
        first.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: "KM6Z"));
        await CreateService(dbContext, first).RunAsync(team, CancellationToken.None);

        var second = new FakeExamToolsClient();
        second.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: null));
        await CreateService(dbContext, second).RunAsync(team, CancellationToken.None);

        Assert.Equal("KM6Z", dbContext.Sessions.Single().TeamLeadCallSign);
    }

    /// <summary>
    /// ExamTools reports the literal "&lt;UNKNOWN&gt;" when it has no call sign, and treating that as
    /// an identity once fused two different people (see Core/CallSign). A lead that is not
    /// call-sign-shaped is stored as-is rather than resolved to anybody — the guard belongs at the
    /// point of matching, but garbage must not look like a real lead either.
    /// </summary>
    [Fact]
    public async Task NewSession_PlaceholderLeadIsNotTreatedAsACallSign()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(teamLeadCallsign: "<UNKNOWN>"));

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.False(CallSign.IsUsable(dbContext.Sessions.Single().TeamLeadCallSign));
    }
}
