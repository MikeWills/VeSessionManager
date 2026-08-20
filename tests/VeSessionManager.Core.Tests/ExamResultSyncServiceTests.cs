using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class ExamResultSyncServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public Dictionary<string, ExamToolsApplicantDetail?> DetailByApplicantId { get; } = [];
        public HashSet<string> FailingApplicantIds { get; } = [];
        public List<string> DetailFetches { get; } = [];
        public List<ExamToolsCredentials> CredentialsUsed { get; } = [];

        public void SetDetail(string applicantId, params ExamToolsExamResult[] exams) =>
            DetailByApplicantId[applicantId] = new ExamToolsApplicantDetail { Exams = [.. exams] };

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by ExamResultSyncService.");

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by ExamResultSyncService.");

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by ExamResultSyncService.");

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by ExamResultSyncService.");

        // Not served by this fake: the VEC archive is only reached from the ARRL submission
        // path (#197), which none of these tests exercise.
        public Task<VecArchiveDownload> DownloadVecArchiveAsync(ExamToolsCredentials credentials, string examToolsSessionId, string vecCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            DetailFetches.Add(applicantId);
            if (FailingApplicantIds.Contains(applicantId))
            {
                throw new InvalidOperationException($"Simulated ExamTools failure for applicant {applicantId}");
            }

            return Task.FromResult(DetailByApplicantId.TryGetValue(applicantId, out var detail) ? detail : null);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool examToolsConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            ExamToolsTeamCode = examToolsConfigured ? "TESTTEAM" : null,
            ExamToolsUsername = examToolsConfigured ? "testuser" : null,
            ExamToolsPassword = examToolsConfigured ? "testpass" : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, string examToolsSessionId = "session-1",
        SessionStatus status = SessionStatus.Active, DateTime? scheduledStartUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
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
        var session = new Session
        {
            ExamToolsSessionId = examToolsSessionId,
            Title = "Test Session",
            ScheduledStartUtc = scheduledStartUtc ?? Now.AddDays(-1),
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

    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext, Session session, string examToolsApplicantId = "applicant-1",
        CandidateApplicationStatus status = CandidateApplicationStatus.Unmatched, bool tested = false,
        LicenseClass? newLicenseClass = null, int? resultMarkedByUserId = null)
    {
        var candidate = new Candidate
        {
            SessionId = session.Id,
            ExamToolsApplicantId = examToolsApplicantId,
            Name = "Test Candidate",
            Email = "candidate@example.com",
            DateRegisteredUtc = Now,
            ApplicationStatus = status,
            Tested = tested,
            NewLicenseClass = newLicenseClass,
            ResultMarkedByUserId = resultMarkedByUserId
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static ExamResultSyncService CreateService(AppDbContext dbContext, FakeExamToolsClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), Options.Create(new ExamToolsOptions()), NullLogger<ExamResultSyncService>.Instance);

    [Fact]
    public async Task GradedFailedExam_MarksCandidateFailed_SetsResultFieldsWithNullUser()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedFailed);
        Assert.Equal(0, result.CandidatesMarkedTested);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Failed, updated.ApplicationStatus);
        Assert.True(updated.Tested);
        Assert.Equal(Now, updated.ResultMarkedUtc);
        Assert.Null(updated.ResultMarkedByUserId); // system-detected, no VE clicked anything
        var audit = Assert.Single(dbContext.AuditLogs);
        Assert.Equal("CandidateAutoMarkedFailed", audit.Action);
        Assert.Null(audit.UserId);
    }

    [Fact]
    public async Task GradedPassedExam_MarksTested_LeavesApplicationStatusAlone()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 2, Graded = true, Passed = true });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Equal(1, result.CandidatesMarkedTested);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus); // unchanged — still waiting on the FCC watcher
        Assert.True(updated.Tested);
        Assert.Null(updated.ResultMarkedUtc); // only set on the Failed path
        Assert.Empty(dbContext.AuditLogs);
        // Passed only Element 2 — no prior credit implied, so walked in Unlicensed and out Technician.
        Assert.Equal(LicenseClass.None, updated.InitialLicenseClass);
        Assert.Equal(LicenseClass.Technician, updated.NewLicenseClass);
    }

    [Theory]
    [InlineData(new[] { 2 }, LicenseClass.None, LicenseClass.Technician)]
    [InlineData(new[] { 2, 3 }, LicenseClass.None, LicenseClass.General)]
    [InlineData(new[] { 2, 3, 4 }, LicenseClass.None, LicenseClass.Extra)]
    [InlineData(new[] { 3 }, LicenseClass.Technician, LicenseClass.General)]
    [InlineData(new[] { 3, 4 }, LicenseClass.Technician, LicenseClass.Extra)]
    [InlineData(new[] { 4 }, LicenseClass.General, LicenseClass.Extra)]
    public async Task GradedPassedExam_ResolvesLicenseClassFromElementsPassedThisSitting(int[] elements, LicenseClass expectedInitial, LicenseClass expectedNew)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!, [.. elements.Select(e => new ExamToolsExamResult { Element = e, Graded = true, Passed = true })]);

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(expectedInitial, updated.InitialLicenseClass);
        Assert.Equal(expectedNew, updated.NewLicenseClass);
    }

    /// <summary>
    /// **The John Davey case** (reported live at HRCC, 2026-08-09): reached for General, missed it, but
    /// passed Technician in the same sitting, so he walks away newly licensed. The old logic keyed on
    /// "did anything fail?" and called him Failed with no license class at all.
    /// </summary>
    [Fact]
    public async Task PassedLowerElementButFailedHigherOne_IsNotFailed_AndEarnsTheClassTheyPassed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!,
            new ExamToolsExamResult { Element = 2, Graded = true, Passed = true },
            new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Equal(1, result.CandidatesMarkedTested);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.NotEqual(CandidateApplicationStatus.Failed, updated.ApplicationStatus);
        Assert.True(updated.Tested);
        // The failed Element 3 must not drag the earned class up to General.
        Assert.Equal(LicenseClass.None, updated.InitialLicenseClass);
        Assert.Equal(LicenseClass.Technician, updated.NewLicenseClass);
    }

    /// <summary>
    /// The retake-within-one-sitting case Mike also described: fail it, sit it again, pass. The failed
    /// attempt is still on the record and must not decide the outcome.
    /// </summary>
    [Fact]
    public async Task FailedThenPassedTheSameElement_CountsAsPassed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!,
            new ExamToolsExamResult { Element = 3, Graded = true, Passed = false },
            new ExamToolsExamResult { Element = 3, Graded = true, Passed = true });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(LicenseClass.General, updated.NewLicenseClass);
    }

    /// <summary>
    /// The self-healing half. Someone the old logic already wrote off is re-examined and put right on
    /// the next poll. Without this, the very people the bug harmed would be the only ones it never
    /// reached, since Failed used to be a permanent exclusion from the scan.
    /// </summary>
    [Fact]
    public async Task AutoFailedCandidateWhoActuallyPassedSomething_IsCorrectedOnTheNextPoll()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session,
            status: CandidateApplicationStatus.Failed, tested: true, resultMarkedByUserId: null);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!,
            new ExamToolsExamResult { Element = 2, Graded = true, Passed = true },
            new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesAutoFailedCorrected);
        Assert.Equal(0, result.CandidatesBackfilledLicenseClass); // counted once, not in both buckets
        var updated = await dbContext.Candidates.SingleAsync();
        // Back to Unmatched so UlsWatcherService carries them on to Received/Granted.
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Equal(LicenseClass.Technician, updated.NewLicenseClass);
        Assert.Null(updated.ResultMarkedUtc);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "CandidateAutoFailedCorrected");
    }

    /// <summary>
    /// The other side of no longer excluding Failed from the scan: a genuinely failed candidate gets
    /// re-polled for the rest of the window, and must not re-audit or re-count an unchanged verdict
    /// every time.
    /// </summary>
    [Fact]
    public async Task AutoFailedCandidateWhoReallyFailedEverything_IsRepolledButNotReAudited()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session,
            status: CandidateApplicationStatus.Failed, tested: true);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!,
            new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Single(client.DetailFetches);
        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Equal(CandidateApplicationStatus.Failed, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
        Assert.DoesNotContain(dbContext.AuditLogs, a => a.Action == "CandidateAutoMarkedFailed");
    }

    [Fact]
    public async Task UngradedExam_LeavesCandidateAlone_RetriesNextPoll()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 3, Graded = false, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Equal(0, result.CandidatesMarkedTested);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.False(updated.Tested);
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
    }

    [Fact]
    public async Task NoExamsYet_LeavesCandidateAlone()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient(); // no SetDetail call — GetApplicantDetailAsync returns null

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Equal(0, result.CandidatesMarkedTested);
        Assert.False((await dbContext.Candidates.SingleAsync()).Tested);
    }

    /// <summary>
    /// A settled candidate costs nothing — still true, on a new boundary.
    ///
    /// <para><b>This used to assert the same thing on an OPEN session</b>, i.e. that a class once
    /// recorded stopped the candidate ever being fetched again. That was the second half of #437:
    /// ExamTools grades element by element, so freezing on the first graded element it happened to
    /// see recorded General for a candidate who passed Element 4 minutes later — and this filter made
    /// it unobservable as well as permanent. The bound is now the session being closed, where grading
    /// genuinely cannot still be in progress. The property the test protects is unchanged.</para>
    /// </summary>
    [Fact]
    public async Task AlreadyTestedCandidateWithLicenseClassAlreadySet_IsNeverRechecked_OnceTheSessionIsClosed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        session.ExamToolsClosedUtc = Now;
        await SeedCandidateAsync(dbContext, session, tested: true, newLicenseClass: LicenseClass.Technician);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    /// <summary>
    /// A HUMAN Failed verdict stays final. Auto-failed rows are re-examined (see the correction tests
    /// above), but a Session Manager who marked someone failed must not be overruled by a feed.
    /// <c>ResultMarkedByUserId</c> is what tells the two apart.
    /// </summary>
    [Fact]
    public async Task ManuallyFailedCandidate_IsNeverRechecked()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session, status: CandidateApplicationStatus.Failed,
            tested: true, resultMarkedByUserId: 42);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task NotTestedApplicationStatus_IsNeverRechecked()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session, status: CandidateApplicationStatus.NotTested);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    /// <summary>
    /// The core of the "update all current, past, and future candidates" backfill (issue reported
    /// 2026-07-29): a candidate who was already Tested/Granted by an older code version — before
    /// InitialLicenseClass/NewLicenseClass existed — gets picked back up exactly once and filled in,
    /// via the same NewLicenseClass-is-null idempotency guard as every other job in this app.
    /// </summary>
    [Fact]
    public async Task AlreadyGrantedCandidateMissingLicenseClass_IsBackfilledOnce()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session, status: CandidateApplicationStatus.Granted, tested: true);
        var client = new FakeExamToolsClient();
        client.SetDetail(candidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 2, Graded = true, Passed = true });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesBackfilledLicenseClass);
        Assert.Equal(0, result.CandidatesMarkedTested); // not a new pass — Tested was already true
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus); // unchanged
        Assert.Equal(LicenseClass.None, updated.InitialLicenseClass);
        Assert.Equal(LicenseClass.Technician, updated.NewLicenseClass);

        // Second run: backfilled ONCE. Since #437 an open session is still re-read (a later-graded
        // element must be able to land), so the thing that must not repeat is the write, not the
        // fetch — assert that directly rather than through the fetch as a proxy.
        client.DetailFetches.Clear();
        var again = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, again.CandidatesBackfilledLicenseClass);
        Assert.Equal(0, again.CandidatesLicenseClassRaised);
        Assert.Equal(LicenseClass.Technician, (await dbContext.Candidates.SingleAsync()).NewLicenseClass);

        // And once the session closes, it is not even fetched.
        session.ExamToolsClosedUtc = Now;
        await dbContext.SaveChangesAsync();
        client.DetailFetches.Clear();
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task FutureSession_IsSkipped_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(1));
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task CancelledSession_IsSkipped_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, status: SessionStatus.Cancelled);
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task UnconfiguredTeam_SkipsSync_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, examToolsConfigured: false);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedFailed);
        Assert.Empty(client.CredentialsUsed);
    }

    [Fact]
    public async Task OneCandidateFailingApiCall_DoesNotBlockOtherCandidates_AndSavesEachIndependently()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidateA = await SeedCandidateAsync(dbContext, session, "applicant-a");
        var candidateB = await SeedCandidateAsync(dbContext, session, "applicant-b");
        var client = new FakeExamToolsClient();
        client.FailingApplicantIds.Add(candidateA.ExamToolsApplicantId!);
        client.SetDetail(candidateB.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedFailed);
        Assert.False((await dbContext.Candidates.FindAsync(candidateA.Id))!.Tested);
        Assert.Equal(CandidateApplicationStatus.Failed, (await dbContext.Candidates.FindAsync(candidateB.Id))!.ApplicationStatus);
    }

    [Fact]
    public async Task CandidateWithNoExamToolsApplicantId_IsSkipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = new Candidate
        {
            SessionId = session.Id,
            ExamToolsApplicantId = null, // manually-created row, per Candidate.cs's own doc comment
            Name = "Manual Candidate",
            DateRegisteredUtc = Now,
            ApplicationStatus = CandidateApplicationStatus.Unmatched,
            Tested = false
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task SessionOlderThanTheResultSyncWindow_IsNotPolledAtAll()
    {
        // Issue #81. Status only ever leaves Active on cancellation, so "Active and already
        // started" meant every session the team had ever run — and any candidate that never
        // resolves (a no-show whose ExamTools record carries no result data) was one API call per
        // tick, forever. The historical import made it worse: imported candidates arrive
        // Tested=false, so a year of history is a burst plus a permanent residue.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team,
            scheduledStartUtc: Now - ExamResultSyncService.ResultSyncWindow - TimeSpan.FromDays(1));
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    [Fact]
    public async Task SessionInsideTheResultSyncWindow_IsStillPolled()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team,
            scheduledStartUtc: Now - ExamResultSyncService.ResultSyncWindow + TimeSpan.FromDays(1));
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Single(client.DetailFetches);
    }

    [Fact]
    public async Task RecentlyImportedButLongPastSession_IsNotPolled_DespiteAFreshClosedStamp()
    {
        // The window is anchored on when the session RAN, not on ExamToolsClosedUtc, precisely
        // because the historical import stamps that field at import time. Anchoring on the close
        // stamp would leave a freshly-imported March session eligible for the full window and
        // preserve the burst this bound exists to stop.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team,
            scheduledStartUtc: Now.AddMonths(-5));
        session.ExamToolsClosedUtc = Now; // just imported
        await dbContext.SaveChangesAsync();
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }

    // ---- SyncSessionAsync (session-scoped Detail-page refresh, 2026-08-03) ----

    /// <summary>
    /// SyncSessionAsync is the on-demand escape hatch ResultSyncWindow's doc comment promises: a
    /// session graded later than the window still gets its results applied when refreshed from the
    /// Detail page — the exact session RunAsync's window bound would skip — and only the named
    /// session is touched.
    /// </summary>
    [Fact]
    public async Task SyncSessionAsync_SessionOlderThanResultSyncWindow_StillAppliesResults_OnlyForThatSession()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var oldSession = await SeedSessionAsync(dbContext, team, "old-session", scheduledStartUtc: Now.AddDays(-60));
        var recentSession = await SeedSessionAsync(dbContext, team, "recent-session", scheduledStartUtc: Now.AddDays(-1));
        var oldCandidate = await SeedCandidateAsync(dbContext, oldSession, "applicant-old");
        var otherCandidate = await SeedCandidateAsync(dbContext, recentSession, "applicant-other");
        var client = new FakeExamToolsClient();
        client.SetDetail(oldCandidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 3, Graded = true, Passed = false });
        client.SetDetail(otherCandidate.ExamToolsApplicantId!, new ExamToolsExamResult { Element = 2, Graded = true, Passed = true });

        var result = await CreateService(dbContext, client).SyncSessionAsync(team, oldSession.Id, CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedFailed);
        Assert.Equal("applicant-old", Assert.Single(client.DetailFetches)); // the other session was never polled
        Assert.Equal(CandidateApplicationStatus.Failed, (await dbContext.Candidates.FindAsync(oldCandidate.Id))!.ApplicationStatus);
        var untouched = (await dbContext.Candidates.FindAsync(otherCandidate.Id))!;
        Assert.False(untouched.Tested);
        Assert.Equal(CandidateApplicationStatus.Unmatched, untouched.ApplicationStatus);
    }

    [Fact]
    public async Task SyncSessionAsync_FutureSession_IsSkipped_NeverCallsClient()
    {
        // Still requires the session to have started — a future session can't have results yet.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(1));
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).SyncSessionAsync(team, session.Id, CancellationToken.None);

        Assert.Empty(client.DetailFetches);
    }
    // ---- Issue #437: a partially-graded sitting froze the class too low -----------------------
    // Reported live on Chang Sun (HRCC, 2026-08-18): E2, E3 and E4 all passed, recorded as General.

    private static ExamToolsExamResult PassedElement(int element) =>
        new() { Element = element, Graded = true, Passed = true };

    /// <summary>The reported case end to end. ExamTools grades element by element as VEs enter
    /// results, so a poll can legitimately see a partial set — it must not become permanent.</summary>
    [Fact]
    public async Task ASecondPollSeeingElement4_RaisesGeneralToExtra()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        var service = CreateService(dbContext, client);

        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3));
        await service.RunAsync(team, CancellationToken.None);
        Assert.Equal(LicenseClass.General, candidate.NewLicenseClass);

        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3), PassedElement(4));
        await service.RunAsync(team, CancellationToken.None);

        Assert.Equal(LicenseClass.Extra, candidate.NewLicenseClass);
        Assert.Equal(LicenseClass.None, candidate.InitialLicenseClass);
    }

    /// <summary>The other half of the freeze, and the one that made it unobservable: the scan filter
    /// stopped fetching a Tested candidate who already had a class, so the later element could not be
    /// seen even in principle.</summary>
    [Fact]
    public async Task AnAlreadyClassifiedCandidate_IsStillFetched_WhileTheSessionIsOpen()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session, tested: true, newLicenseClass: LicenseClass.General);
        var client = new FakeExamToolsClient();
        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3));

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Contains("applicant-1", client.DetailFetches);
    }

    /// <summary>A revision is a correction, not a fresh result — the ops dashboard must not read it as
    /// one, the same reason CandidatesAutoFailedCorrected is counted apart.</summary>
    [Fact]
    public async Task ARevision_IsCountedSeparatelyFromAFreshResult()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        var service = CreateService(dbContext, client);

        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3));
        await service.RunAsync(team, CancellationToken.None);

        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3), PassedElement(4));
        var second = await service.RunAsync(team, CancellationToken.None);

        Assert.Equal(1, second.CandidatesLicenseClassRaised);
        Assert.Equal(0, second.CandidatesMarkedTested);
    }

    /// <summary>An unchanged re-read writes nothing — this now polls every tick while a session is open.</summary>
    [Fact]
    public async Task AnUnchangedReRead_RaisesNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, session);
        var client = new FakeExamToolsClient();
        var service = CreateService(dbContext, client);

        client.SetDetail("applicant-1", PassedElement(2), PassedElement(3));
        await service.RunAsync(team, CancellationToken.None);
        var second = await service.RunAsync(team, CancellationToken.None);

        Assert.Equal(0, second.CandidatesLicenseClassRaised);
    }
}
