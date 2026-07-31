using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.FccUls;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class FccUlsWatcherServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc); // a Monday

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeFccUlsClient : IFccUlsClient
    {
        public IReadOnlyList<FccUlsApplicationRecord>? DailyApplications { get; set; } = [];
        public IReadOnlyList<FccUlsLicenseRecord>? DailyLicenses { get; set; } = [];
        public IReadOnlyList<FccUlsApplicationRecord>? WeeklyApplications { get; set; } = [];
        public IReadOnlyList<FccUlsLicenseRecord>? WeeklyLicenses { get; set; } = [];
        public List<DayOfWeek> DailyApplicationCallDays { get; } = [];
        public List<DayOfWeek> DailyLicenseCallDays { get; } = [];
        public int WeeklyApplicationCalls { get; private set; }
        public int WeeklyLicenseCalls { get; private set; }

        public Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadDailyApplicationsAsync(DayOfWeek day, CancellationToken cancellationToken)
        {
            DailyApplicationCallDays.Add(day);
            return Task.FromResult(DailyApplications);
        }

        public Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadDailyLicensesAsync(DayOfWeek day, CancellationToken cancellationToken)
        {
            DailyLicenseCallDays.Add(day);
            return Task.FromResult(DailyLicenses);
        }

        public Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadWeeklyApplicationsAsync(CancellationToken cancellationToken)
        {
            WeeklyApplicationCalls++;
            return Task.FromResult(WeeklyApplications);
        }

        public Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadWeeklyLicensesAsync(CancellationToken cancellationToken)
        {
            WeeklyLicenseCalls++;
            return Task.FromResult(WeeklyLicenses);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static FccUlsWatcherService CreateService(AppDbContext dbContext, IFccUlsClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<FccUlsWatcherService>.Instance);

    /// <summary>Seeds Vec/User/FeeConfiguration/Session/Candidate with the given Frn/ApplicationStatus. Session defaults to
    /// four days *before* Now (i.e. testing already happened) since application-file matching requires the matched
    /// record's Last Action Date to be on/after the session date — pass sessionStartUtc to control that explicitly.</summary>
    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext, string? frn, CandidateApplicationStatus status = CandidateApplicationStatus.Unmatched,
        DateTime? sessionStartUtc = null,
        LicenseClass? initialLicenseClass = null, LicenseClass? newLicenseClass = null)
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
            ExamToolsSessionId = "session-1",
            Title = "July Session",
            ScheduledStartUtc = sessionStartUtc ?? Now.AddDays(-4),
            DurationMinutes = 60,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            CreatedUtc = Now
        };
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1",
            Session = session,
            Name = "Roana Glory",
            Frn = frn,
            ApplicationStatus = status,
            InitialLicenseClass = initialLicenseClass,
            NewLicenseClass = newLicenseClass,
            DateRegisteredUtc = Now
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    [Fact]
    public async Task UnmatchedCandidate_FrnInApplicationFile_BecomesReceived_WithLastActionDate()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567");
        var lastActionDate = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", lastActionDate)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedReceived);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Equal(lastActionDate, updated.ApplicationDateEnteredUtc);
    }

    [Fact]
    public async Task ApplicationRecord_LastActionDatePredatesSession_DoesNotMatch_StaysUnmatched()
    {
        // Simulates a stale/dismissed prior application for the same FRN, months before this
        // candidate's own session — see the stale-application gotcha in FccUlsWatcherService's
        // doc comment (found via a live FRN lookup on 2026-07-22).
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 22, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", sessionStartUtc: sessionStart);
        var staleLastActionDate = new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", staleLastActionDate)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedReceived);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Null(updated.ApplicationDateEnteredUtc);
    }

    [Fact]
    public async Task MultipleApplicationRecordsForSameFrn_PicksMostRecentLastActionDate()
    {
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 22, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", sessionStartUtc: sessionStart);
        var staleLastActionDate = new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc);
        var freshLastActionDate = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications =
            [
                new FccUlsApplicationRecord("100", "0001234567", staleLastActionDate),
                new FccUlsApplicationRecord("101", "0001234567", freshLastActionDate)
            ]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedReceived);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Equal(freshLastActionDate, updated.ApplicationDateEnteredUtc);
    }

    [Fact]
    public async Task ReceivedCandidate_HoldReasonRefreshesFromApplicationFile_EvenWithoutAStatusTransition()
    {
        // FccHoldReason must refresh every run, not just on the Unmatched->Received transition — a
        // Red Light/Basic Qualification hold can be placed or cleared well after the candidate was
        // first matched. See FccUlsWatcherService.ProcessApplicationsAsync.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart);
        var lastActionDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", lastActionDate, FccApplicationHoldReason.RedLight)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedReceived); // already Received — no transition
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Equal(FccApplicationHoldReason.RedLight, updated.FccHoldReason);
    }

    [Fact]
    public async Task ReceivedCandidate_PaymentStatusRefreshesFromApplicationFile_EvenWithoutAStatusTransition()
    {
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart);
        var lastActionDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", lastActionDate, PaymentStatus: FccApplicationPaymentStatus.Paid)]
        };

        await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(FccApplicationPaymentStatus.Paid, updated.FccPaymentStatus);
    }

    [Fact]
    public async Task ReceivedCandidate_HoldReasonClears_WhenApplicationFileNoLongerShowsAHold()
    {
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart);
        candidate.FccHoldReason = FccApplicationHoldReason.RedLight;
        await dbContext.SaveChangesAsync();
        var lastActionDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", lastActionDate, FccApplicationHoldReason.None)]
        };

        await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(FccApplicationHoldReason.None, updated.FccHoldReason);
    }

    [Fact]
    public async Task ReceivedCandidate_FrnInActiveLicenseFile_BecomesGranted_WithCallSignAndGrantDate()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received);
        var grantDate = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", grantDate, grantDate)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        Assert.Equal("K0BFR", updated.CallSign);
        Assert.Equal(grantDate, updated.LicenseGrantDateUtc);
        Assert.Equal("100", updated.FccUlsLicenseKey);
    }

    [Fact]
    public async Task LicenseRecord_GrantDatePredatesSession_DoesNotMatch_StaysUnmatched()
    {
        // Simulates a real "upgrade exam" candidate (already licensed, testing to move up a class):
        // their FRN has an Active record in the license file the whole time, from their *original*
        // grant. Without this guard, that stale record would immediately mark them Granted the
        // moment any watcher run touched their row, even though FCC hasn't processed today's
        // upgrade at all. Confirmed live 2026-07-30 against three real same-day upgrade candidates
        // whose Grant Dates predated their session by anywhere from weeks to years.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 22, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", sessionStartUtc: sessionStart);
        var priorGrantDate = new DateTime(2024, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", priorGrantDate, priorGrantDate)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Null(updated.CallSign);
        Assert.Null(updated.LicenseGrantDateUtc);
        Assert.Null(updated.FccUlsLicenseKey);
    }

    // ---- Upgrade confirmation via AM.dat operator class + Last Action Date (2026-07-30) ----
    // Real-data shape these are modeled on: a General->Extra upgrade taken 2026-07-19 still reported
    // a Grant Date of 2021-04-30 (FCC pins Grant Date to the original license) but a Last Action Date
    // of 2026-07-21. Grant Date alone can therefore never confirm an upgrade — see
    // FccUlsWatcherService.ProcessLicensesAsync.

    [Fact]
    public async Task UpgradeCandidate_OperatorClassMatchesAndLastActionAfterSession_BecomesGranted()
    {
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 19, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.General, newLicenseClass: LicenseClass.Extra);
        var originalGrant = new DateTime(2021, 4, 30, 0, 0, 0, DateTimeKind.Utc);
        var upgradeAction = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "N2LQH", "A", originalGrant, upgradeAction, LicenseClass.Extra)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        Assert.Equal("N2LQH", updated.CallSign);
        // The upgrade date, NOT the 2021 original grant — otherwise the UI reads "licensed 2021".
        Assert.Equal(upgradeAction, updated.LicenseGrantDateUtc);
    }

    [Fact]
    public async Task UpgradeCandidate_FccStillReportsOldClass_StaysPending()
    {
        // Katelynn Schneider's real shape: tested Technician->General on 2026-07-30, but FCC hadn't
        // processed it — still class T, with grant/last-action both predating the session. Must be
        // rejected on BOTH the class and the date, so neither check is load-bearing alone.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.Technician, newLicenseClass: LicenseClass.General);
        var priorDate = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "KR4NQF", "A", priorDate, priorDate, LicenseClass.Technician)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Null(updated.CallSign);
    }

    [Fact]
    public async Task UpgradeCandidate_ClassMatchesButLastActionPredatesSession_StaysPending()
    {
        // The class check alone is not enough: someone who ALREADY held Extra walking in would match
        // on class forever. Only the date proves this sitting caused it.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.General, newLicenseClass: LicenseClass.Extra);
        var staleDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "N2LQH", "A", staleDate, staleDate, LicenseClass.Extra)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        Assert.Equal(CandidateApplicationStatus.Received, (await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).ApplicationStatus);
    }

    [Fact]
    public async Task UpgradeCandidate_NoOperatorClassAvailable_StaysPending()
    {
        // An archive without AM.dat yields OperatorClass None — must fall back to the old
        // stays-pending behavior rather than matching on the date alone.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 19, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.General, newLicenseClass: LicenseClass.Extra);
        var client = new FakeFccUlsClient
        {
            DailyLicenses =
            [
                new FccUlsLicenseRecord("100", "0001234567", "N2LQH", "A",
                    new DateTime(2021, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc))
            ]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        Assert.Equal(CandidateApplicationStatus.Received, (await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).ApplicationStatus);
    }

    [Fact]
    public async Task NewLicenseCandidate_GrantDateOnOrAfterSession_StillGrantedWithGrantDate()
    {
        // Regression guard: the upgrade work must not change the first-time-licensee path, which
        // keeps using Grant Date (not Last Action Date) for LicenseGrantDateUtc.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 19, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received, sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.None, newLicenseClass: LicenseClass.Technician);
        var grantDate = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "KF0NEW", "A", grantDate, grantDate, LicenseClass.Technician)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        Assert.Equal(grantDate, updated.LicenseGrantDateUtc);
    }

    // ---- Full-week daily sweep (2026-07-30) ----
    // The weekly "complete" snapshot is regenerated only weekly and stamps its own creation date:
    // the one fetched on Thu 2026-07-30 read "Sun Jul 26" with no data past 07/25. RunDailyAsync only
    // reads yesterday+today. Anything FCC acted on in between is in NO file either path reads — three
    // real upgrades sat pending with their data in l_am_mon.zip/l_am_tue.zip the whole time.

    [Fact]
    public async Task RunAllDailyFiles_RequestsEveryPublishedDay_MondayThroughSaturday()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient();

        await CreateService(dbContext, client).RunAllDailyFilesAsync(CancellationToken.None);

        DayOfWeek[] expected =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday
        ];
        Assert.Equal(expected, client.DailyLicenseCallDays);
        Assert.Equal(expected, client.DailyApplicationCallDays);
        // No Sunday file exists — FCC publishes Tue-Sat covering Mon-Fri, so asking is a wasted call.
        Assert.DoesNotContain(DayOfWeek.Sunday, client.DailyLicenseCallDays);
    }

    [Fact]
    public async Task RunAllDailyFiles_GrantsAnUpgradeSittingInAMidWeekFile()
    {
        // Jason Pelowitz's real shape: session Jul 24, upgrade recorded 07/27 in l_am_mon.zip —
        // newer than the weekly snapshot, older than "yesterday", so previously invisible.
        await using var dbContext = CreateContext();
        var sessionStart = new DateTime(2026, 7, 24, 18, 0, 0, DateTimeKind.Utc);
        var candidate = await SeedCandidateAsync(
            dbContext, frn: "0001234567", sessionStartUtc: sessionStart,
            initialLicenseClass: LicenseClass.Technician, newLicenseClass: LicenseClass.General);
        var client = new FakeFccUlsClient
        {
            DailyApplications = null,
            DailyLicenses =
            [
                new FccUlsLicenseRecord("100", "0001234567", "KJ5RDA", "A",
                    new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
                    LicenseClass.General)
            ]
        };

        var result = await CreateService(dbContext, client).RunAllDailyFilesAsync(CancellationToken.None);

        // Granted exactly once despite the same record appearing in all six day files — the scan only
        // ever selects non-terminal candidates, so later days are a no-op.
        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        Assert.Equal("KJ5RDA", updated.CallSign);
    }

    [Fact]
    public async Task WeeklyCatchup_AlsoSweepsDailyFiles_NotJustTheSnapshot()
    {
        // The snapshot alone can't be a backstop when it arrives days stale; the daily sweep is what
        // makes the weekly job actually catch up.
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient();

        await CreateService(dbContext, client).RunWeeklyCatchupAsync(CancellationToken.None);

        Assert.Equal(1, client.WeeklyLicenseCalls);
        Assert.Equal(6, client.DailyLicenseCallDays.Count);
    }

    [Fact]
    public async Task UnmatchedCandidate_FrnInActiveLicenseFile_ShortCircuitsStraightToGranted()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567"); // still Unmatched
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", Now, Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
    }

    [Fact]
    public async Task SameRun_ApplicationThenLicenseMatch_GoesUnmatchedToReceivedToGranted()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", Now)],
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", Now, Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedReceived);
        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
    }

    [Fact]
    public async Task LicenseFile_CanceledStatus_DoesNotCountAsGrant()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "C", Now, Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        var candidate = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Unmatched, candidate.ApplicationStatus);
    }

    [Theory]
    [InlineData(CandidateApplicationStatus.Granted)]
    [InlineData(CandidateApplicationStatus.Failed)]
    [InlineData(CandidateApplicationStatus.NotTested)]
    public async Task TerminalStatusCandidates_AreNeverTouched_EvenWithAMatchingFrn(CandidateApplicationStatus terminalStatus)
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: terminalStatus);
        candidate.CallSign = "N0PRE"; // pre-existing value that must not be overwritten
        await dbContext.SaveChangesAsync();
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", Now)],
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0NEW", "A", Now, Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedReceived);
        Assert.Equal(0, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(terminalStatus, updated.ApplicationStatus);
        Assert.Equal("N0PRE", updated.CallSign);
    }

    [Fact]
    public async Task CandidateWithNullFrn_IsSkippedEntirely()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: null);
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0001234567", Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedReceived);
        var candidate = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Unmatched, candidate.ApplicationStatus);
    }

    [Fact]
    public async Task NonMatchingFrn_LeavesCandidateUnmatched()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: "0001111111");
        var client = new FakeFccUlsClient
        {
            DailyApplications = [new FccUlsApplicationRecord("100", "0009999999", Now)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedReceived);
    }

    [Fact]
    public async Task DailyFileUnavailable_IsNotAFailure_ResultReflectsUnavailability()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient { DailyApplications = null, DailyLicenses = null };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.False(result.ApplicationFileAvailable);
        Assert.False(result.LicenseFileAvailable);
        Assert.Equal(0, result.CandidatesMarkedReceived);
        Assert.Equal(0, result.CandidatesMarkedGranted);
    }

    [Fact]
    public async Task RunDailyAsync_RequestsYesterdayAndTodayFromTimeProvider()
    {
        // Checks yesterday's day-name file too, not just today's — see RunDailyAsync's own remarks
        // on why a same-day-only check can permanently miss a late-published grant.
        await using var dbContext = CreateContext();
        var client = new FakeFccUlsClient();

        await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday], client.DailyApplicationCallDays);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday], client.DailyLicenseCallDays);
        Assert.Equal(0, client.WeeklyApplicationCalls);
        Assert.Equal(0, client.WeeklyLicenseCalls);
    }

    [Fact]
    public async Task RunDailyAsync_NearUtcMidnight_UsesEasternDayOfWeek_NotUtcDayOfWeek()
    {
        // 2026-07-23 00:30 UTC is 2026-07-22 20:30 EDT (UTC-4 in July) — still Wednesday evening in
        // US Eastern, even though the UTC calendar date has already rolled over to Thursday. Found
        // live 2026-07-23: FccDailyWatcherJob's evening retry (default 8pm ET) lands right around
        // this boundary for most of the year, so using raw UTC here would silently fetch the wrong
        // (not-yet-published, or already-superseded-a-week-later) day-name file. See
        // docs/fcc-uls-watcher.md.
        await using var dbContext = CreateContext();
        var lateEveningUtc = new DateTime(2026, 7, 23, 0, 30, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient();
        var service = new FccUlsWatcherService(dbContext, client, new FixedTimeProvider(lateEveningUtc), NullLogger<FccUlsWatcherService>.Instance);

        await service.RunDailyAsync(CancellationToken.None);

        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Wednesday], client.DailyApplicationCallDays);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Wednesday], client.DailyLicenseCallDays);
    }

    [Fact]
    public async Task RunWeeklyCatchupAsync_HitsTheWeeklySnapshotEndpointsExactlyOnce()
    {
        // This test previously also asserted the weekly catch-up touches NO daily endpoint. That
        // assertion encoded the very premise that turned out to be wrong (2026-07-30): the weekly
        // snapshot can arrive days stale, so a snapshot-only "catch-up" cannot actually catch up.
        // The daily sweep is now part of the job — covered by
        // WeeklyCatchup_AlsoSweepsDailyFiles_NotJustTheSnapshot — so what's pinned here is narrower:
        // the snapshot is still fetched, and fetched only once.
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567");
        var client = new FakeFccUlsClient
        {
            WeeklyApplications = [new FccUlsApplicationRecord("100", "0001234567", Now)]
        };

        var result = await CreateService(dbContext, client).RunWeeklyCatchupAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedReceived);
        Assert.Equal(1, client.WeeklyApplicationCalls);
        Assert.Equal(1, client.WeeklyLicenseCalls);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
    }
}
