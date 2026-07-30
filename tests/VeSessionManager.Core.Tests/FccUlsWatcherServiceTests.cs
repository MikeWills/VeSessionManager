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
        DateTime? sessionStartUtc = null)
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
    public async Task ReceivedCandidate_FrnInActiveLicenseFile_BecomesGranted_WithCallSignAndGrantDate()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567", status: CandidateApplicationStatus.Received);
        var grantDate = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", grantDate)]
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
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", priorGrantDate)]
        };

        var result = await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Null(updated.CallSign);
        Assert.Null(updated.LicenseGrantDateUtc);
        Assert.Null(updated.FccUlsLicenseKey);
    }

    [Fact]
    public async Task UnmatchedCandidate_FrnInActiveLicenseFile_ShortCircuitsStraightToGranted()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext, frn: "0001234567"); // still Unmatched
        var client = new FakeFccUlsClient
        {
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", Now)]
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
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "A", Now)]
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
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0BFR", "C", Now)]
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
            DailyLicenses = [new FccUlsLicenseRecord("100", "0001234567", "K0NEW", "A", Now)]
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
    public async Task RunDailyAsync_RequestsCurrentDayOfWeekFromTimeProvider()
    {
        await using var dbContext = CreateContext();
        var client = new FakeFccUlsClient();

        await CreateService(dbContext, client).RunDailyAsync(CancellationToken.None);

        Assert.Equal([DayOfWeek.Monday], client.DailyApplicationCallDays);
        Assert.Equal([DayOfWeek.Monday], client.DailyLicenseCallDays);
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

        Assert.Equal([DayOfWeek.Wednesday], client.DailyApplicationCallDays);
        Assert.Equal([DayOfWeek.Wednesday], client.DailyLicenseCallDays);
    }

    [Fact]
    public async Task RunWeeklyCatchupAsync_CallsWeeklyEndpoints_NotDaily()
    {
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
        Assert.Empty(client.DailyApplicationCallDays);
        Assert.Empty(client.DailyLicenseCallDays);
        var updated = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
    }
}
