using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #248: the watcher compared FCC's wall-clock dates against
/// <c>Session.ScheduledStartUtc.Date</c>, which is a <b>UTC</b> calendar date.
///
/// <para>Every FCC date arrives date-only and is stamped at UTC midnight by
/// <c>ExamToolsUlsLookupClient.AsUtcDate</c> — it already <i>is</i> a wall-clock date. The session
/// side is a real instant, so <c>.Date</c> on it answers "what day is it in London". For any session
/// at or after ~20:00 ET that is <b>tomorrow</b>, and every comparison in this file is a
/// <c>&gt;=</c> or <c>&lt;</c> against it.</para>
///
/// <para><b>This is not an edge case for this deployment.</b> 697 of 867 stored sessions start
/// between 23:00 and 04:00 UTC — evening ET is simply when volunteer-run exam sessions happen. The
/// consequence was that an evening session's candidates could never match an application FCC
/// received that same evening, so they stayed <c>Unmatched</c> permanently: no
/// <c>FccHoldReason</c>, no <c>UlsApplicationFileNumber</c>, and — because the fee reminder keys off
/// <c>FccPaymentStatus = PendingVerification</c> — no FCC-fee reminder could ever fire for them
/// (#219).</para>
///
/// <para>The pre-existing <c>UlsWatcherServiceTests</c> all passed with the bug present. Its fixture
/// session is 02:30 UTC — itself an evening-ET session — but every date it asserts against sits far
/// enough either side of the boundary that the one-day shift never changed an outcome. These tests
/// sit exactly on it.</para>
/// </summary>
public class UlsWatcherEasternDateTests
{
    /// <summary>
    /// Thursday 2026-07-30 <b>21:00 ET</b>, which is Friday 2026-07-31 01:00 UTC. The session
    /// happened on the 30th; only a UTC reading calls it the 31st.
    /// </summary>
    private static readonly DateTime EveningEtSessionStart = new(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>The session's real calendar day in Eastern time — what FCC would stamp that night.</summary>
    private static readonly DateTime SessionDayEt = new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakeUlsLookupClient(UlsLookupResult? result) : IUlsLookupClient
    {
        public Task<UlsLookupResult?> LookupAsync(string frnOrCallSign, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<Candidate> SeedAsync(AppDbContext dbContext, LicenseClass? newClass = LicenseClass.Technician)
    {
        var team = new Team { Name = "Test Team", ExamToolsTeamCode = "TEST" };
        var session = new Session
        {
            Team = team,
            ScheduledStartUtc = EveningEtSessionStart,
            ExamToolsSessionId = "s-evening",
            Title = "Evening session"
        };
        var candidate = new Candidate
        {
            Session = session,
            Name = "Evening Candidate",
            Frn = "0038704029",
            Tested = true,
            ApplicationStatus = CandidateApplicationStatus.Unmatched,
            InitialLicenseClass = LicenseClass.None,
            NewLicenseClass = newClass
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static UlsWatcherService Service(AppDbContext dbContext, UlsLookupResult? lookup) =>
        new(dbContext, new FakeUlsLookupClient(lookup), TimeProvider.System,
            NullLogger<UlsWatcherService>.Instance);

    /// <summary>
    /// The headline case. FCC receives the application the same evening the exam was sat; before the
    /// fix, <c>receiptDate.Date (Jul 30) &lt; ScheduledStartUtc.Date (Jul 31)</c> rejected it, and
    /// the candidate stayed Unmatched for good.
    /// </summary>
    [Fact]
    public async Task ApplicationReceivedOnTheSessionsEasternDay_IsMatched()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext);

        await Service(dbContext, new UlsLookupResult
        {
            Found = true,
            LicenseStatus = "Active",
            PendingApplications =
            [
                new UlsPendingApplication { UlsFileNumber = "0012131564", ReceiptDateUtc = SessionDayEt }
            ]
        }).RunAsync(CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Equal("0012131564", updated.UlsApplicationFileNumber);
        Assert.Equal(SessionDayEt, updated.ApplicationDateEnteredUtc);
    }

    /// <summary>
    /// The guard the original rule exists for still holds: an application FCC received the day
    /// *before* the session cannot have come from it. Fixing the timezone must not turn this into an
    /// off-by-one that swallows a genuinely older application.
    /// </summary>
    [Fact]
    public async Task ApplicationReceivedTheDayBeforeTheSession_IsStillRejected()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext);

        await Service(dbContext, new UlsLookupResult
        {
            Found = true,
            LicenseStatus = "Active",
            PendingApplications =
            [
                new UlsPendingApplication { UlsFileNumber = "old", ReceiptDateUtc = SessionDayEt.AddDays(-1) }
            ]
        }).RunAsync(CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Null(updated.UlsApplicationFileNumber);
    }

    /// <summary>A licence granted on the session's Eastern day is a grant from that session.</summary>
    [Fact]
    public async Task LicenseGrantedOnTheSessionsEasternDay_MarksGranted()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext);

        var result = await Service(dbContext, new UlsLookupResult
        {
            Found = true,
            LicenseStatus = "Active",
            CallSign = "KC1ZYU",
            OperatorClass = LicenseClass.Technician,
            GrantDateUtc = SessionDayEt
        }).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        Assert.Equal(CandidateApplicationStatus.Granted, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
    }

    /// <summary>
    /// The upgrade path, which the class remarks call out as the case that left 20 real candidates
    /// stuck: grant date is pinned to the original licence and never advances, so only the effective
    /// date can confirm an upgrade — and it lands on the session's Eastern day.
    /// </summary>
    [Fact]
    public async Task UpgradeEffectiveOnTheSessionsEasternDay_MarksGranted()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext, newClass: LicenseClass.General);

        var result = await Service(dbContext, new UlsLookupResult
        {
            Found = true,
            LicenseStatus = "Active",
            CallSign = "KC1ZYU",
            OperatorClass = LicenseClass.General,
            GrantDateUtc = new DateTime(2024, 8, 21, 0, 0, 0, DateTimeKind.Utc),
            EffectiveDateUtc = SessionDayEt
        }).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        // The upgrade's effective date, not the 2024 grant date the class remarks warn about.
        Assert.Equal(SessionDayEt, updated.LicenseGrantDateUtc);
    }

    /// <summary>
    /// Sanity check on the premise, so a future reader does not have to trust the arithmetic in the
    /// comments: the fixture instant really is the previous day in Eastern time.
    /// </summary>
    [Fact]
    public void TheFixtureSessionIsTheDayBeforeInEasternTime()
    {
        Assert.Equal(new DateTime(2026, 7, 31), EveningEtSessionStart.Date);
        Assert.Equal(new DateTime(2026, 7, 30), UlsSchedule.ToEasternDate(EveningEtSessionStart));
    }
}
