using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Replaces FccUlsWatcherServiceTests (2026-07-31). The data source changed but the grant rules did
/// not, so the guards below are ports of that file's assertions — each one was bought with a real
/// production incident and must keep failing loudly if relaxed. See docs/uls-watcher.md.
/// </summary>
public class UlsWatcherServiceTests
{
    private static readonly DateTime SessionStart = new(2026, 7, 30, 2, 30, 0, DateTimeKind.Utc);

    private sealed class FakeUlsLookupClient : IUlsLookupClient
    {
        private readonly Dictionary<string, UlsLookupResult?> _byFrn;
        public List<string> LookedUpFrns { get; } = [];

        public FakeUlsLookupClient(Dictionary<string, UlsLookupResult?> byFrn) => _byFrn = byFrn;

        public Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken)
        {
            LookedUpFrns.Add(frn);
            return Task.FromResult(_byFrn.TryGetValue(frn, out var r) ? r : UlsLookupResult.NotFound);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static UlsWatcherService CreateService(AppDbContext dbContext, IUlsLookupClient client) =>
        new(dbContext, client, NullLogger<UlsWatcherService>.Instance);

    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext,
        CandidateApplicationStatus status = CandidateApplicationStatus.Unmatched,
        LicenseClass? newLicenseClass = LicenseClass.Technician,
        LicenseClass initialLicenseClass = LicenseClass.None,
        string frn = "0038704029")
    {
        var team = new Team { Name = "Test Team", ExamToolsTeamCode = "TEST" };
        var session = new Session { Team = team, ScheduledStartUtc = SessionStart, ExamToolsSessionId = "s1", Title = "Test Session" };
        var candidate = new Candidate
        {
            Session = session,
            Name = "Test Candidate",
            Frn = frn,
            Tested = true,
            ApplicationStatus = status,
            InitialLicenseClass = initialLicenseClass,
            NewLicenseClass = newLicenseClass
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static UlsLookupResult ActiveLicense(
        DateTime? grantDate = null,
        DateTime? effectiveDate = null,
        LicenseClass operatorClass = LicenseClass.Technician,
        string callSign = "KC1ZYU") => new()
        {
            Found = true,
            UniqueSystemIdentifier = 5339614,
            CallSign = callSign,
            LicenseStatus = "Active",
            OperatorClass = operatorClass,
            GrantDateUtc = grantDate,
            EffectiveDateUtc = effectiveDate
        };

    [Fact]
    public async Task NewLicense_GrantedOnOrAfterSession_MarksGranted()
    {
        await using var dbContext = CreateContext();
        var candidate = await SeedCandidateAsync(dbContext);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = ActiveLicense(grantDate: new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc))
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        Assert.Equal("KC1ZYU", updated.CallSign);
        Assert.Equal("5339614", updated.FccUlsLicenseKey);
        Assert.Equal(new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc), updated.LicenseGrantDateUtc);
    }

    /// <summary>
    /// The regression guard for the 2026-07-30 incident: three real upgrade candidates were wrongly
    /// marked Granted off a license granted years earlier, because a pre-existing Active record
    /// looks identical to a fresh one apart from its date.
    /// </summary>
    [Fact]
    public async Task PreExistingLicense_GrantedBeforeSession_DoesNotMarkGranted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, newLicenseClass: LicenseClass.General, initialLicenseClass: LicenseClass.Technician);
        var client = new FakeUlsLookupClient(new()
        {
            // Holds Technician from 2024 and is testing for General — nothing has happened yet.
            ["0038704029"] = ActiveLicense(
                grantDate: new DateTime(2024, 8, 21, 0, 0, 0, DateTimeKind.Utc),
                effectiveDate: new DateTime(2024, 8, 21, 0, 0, 0, DateTimeKind.Utc),
                operatorClass: LicenseClass.Technician)
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        Assert.Equal(CandidateApplicationStatus.Unmatched, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
    }

    /// <summary>
    /// The other half of the 2026-07-30 story: the guard above, shipped alone, made upgrades
    /// permanently undetectable because FCC never advances Grant Date on an upgrade. An upgrade is
    /// confirmed by class + effective date instead.
    /// </summary>
    [Fact]
    public async Task Upgrade_ClassMatchesAndEffectiveDateOnOrAfterSession_MarksGranted_UsingEffectiveDateAsGrantDate()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, newLicenseClass: LicenseClass.Extra, initialLicenseClass: LicenseClass.General);
        var effective = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = ActiveLicense(
                grantDate: new DateTime(2024, 8, 21, 0, 0, 0, DateTimeKind.Utc),
                effectiveDate: effective,
                operatorClass: LicenseClass.Extra,
                callSign: "KF0RMJ")
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Granted, updated.ApplicationStatus);
        // Not the 2024 grant date — that would read as "licensed in 2024" for a 2026 upgrade.
        Assert.Equal(effective, updated.LicenseGrantDateUtc);
    }

    /// <summary>Class alone is insufficient — it would re-confirm someone who already held it walking in.</summary>
    [Fact]
    public async Task Upgrade_ClassMatchesButEffectiveDatePredatesSession_DoesNotMarkGranted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, newLicenseClass: LicenseClass.General, initialLicenseClass: LicenseClass.Technician);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = ActiveLicense(
                grantDate: new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
                effectiveDate: new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
                operatorClass: LicenseClass.General)
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
    }

    /// <summary>Date alone is insufficient — any unrelated administrative action would match.</summary>
    [Fact]
    public async Task Upgrade_EffectiveDateAfterSessionButClassStillOld_DoesNotMarkGranted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, newLicenseClass: LicenseClass.General, initialLicenseClass: LicenseClass.Technician);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = ActiveLicense(
                grantDate: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                effectiveDate: new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
                operatorClass: LicenseClass.Technician)
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
    }

    [Fact]
    public async Task NonActiveLicenseStatus_DoesNotMarkGranted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = ActiveLicense(grantDate: new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc)) with { LicenseStatus = "Pending" }
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
    }

    [Fact]
    public async Task PendingApplication_ReceivedOnOrAfterSession_MarksReceivedAndRecordsFileNumber()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var receipt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = new UlsLookupResult
            {
                Found = true,
                LicenseStatus = "Pending",
                PendingApplications =
                [
                    new UlsPendingApplication
                    {
                        UlsFileNumber = "0012131564",
                        ReceiptDateUtc = receipt,
                        History = [new UlsHistoryEntry(receipt, "RDLCOM")]
                    }
                ]
            }
        });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Received, updated.ApplicationStatus);
        Assert.Equal(receipt, updated.ApplicationDateEnteredUtc);
        Assert.Equal("0012131564", updated.UlsApplicationFileNumber);
    }

    /// <summary>
    /// Ported stale-application guard: an old dismissed application can share an FRN with a genuine
    /// new one, and a real post-exam application cannot predate the exam that produced it.
    /// </summary>
    [Fact]
    public async Task PendingApplication_ReceivedBeforeSession_DoesNotMarkReceived()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = new UlsLookupResult
            {
                Found = true,
                LicenseStatus = "Pending",
                PendingApplications =
                [
                    new UlsPendingApplication { UlsFileNumber = "old", ReceiptDateUtc = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc) }
                ]
            }
        });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(CandidateApplicationStatus.Unmatched, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
    }

    [Fact]
    public async Task HoldCodes_OffWithoutLaterComplete_SetsHoldReason()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var receipt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var client = new FakeUlsLookupClient(new()
        {
            ["0038704029"] = new UlsLookupResult
            {
                Found = true,
                LicenseStatus = "Pending",
                PendingApplications =
                [
                    new UlsPendingApplication
                    {
                        ReceiptDateUtc = receipt,
                        History =
                        [
                            new UlsHistoryEntry(receipt, "RDLOFF"),
                            new UlsHistoryEntry(receipt, "RDLCOM"),   // red light cleared
                            new UlsHistoryEntry(receipt, "BQOFF")     // BQQ still open
                        ]
                    }
                ]
            }
        });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(FccApplicationHoldReason.BasicQualification, (await dbContext.Candidates.SingleAsync()).FccHoldReason);
    }

    [Fact]
    public async Task TerminalCandidates_AreNeverLookedUp()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted);
        var client = new FakeUlsLookupClient([]);

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Empty(client.LookedUpFrns);
        Assert.Equal(0, result.CandidatesChecked);
    }

    /// <summary>
    /// A failed lookup must leave the candidate untouched so the next run retries — distinct from
    /// "FCC has no record", which is a legitimate no-change answer.
    /// </summary>
    [Fact]
    public async Task LookupFailure_LeavesCandidateUnchangedAndIsCounted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var client = new FakeUlsLookupClient(new() { ["0038704029"] = null });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.LookupFailures);
        Assert.Equal(0, result.CandidatesMarkedGranted);
        Assert.Equal(CandidateApplicationStatus.Unmatched, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
    }

    /// <summary>
    /// The license key is captured whenever ULS reports one, **not only on grant** — an upgrade
    /// candidate already holds a license while their upgrade is pending, and Applicant Status renders
    /// its "view in FCC ULS" link off exactly this field. Verified live that `u_id` is the `licKey`
    /// FCC's URL takes.
    /// </summary>
    [Fact]
    public async Task PendingUpgrade_CapturesLicenseKey_WithoutMarkingGranted()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext, newLicenseClass: LicenseClass.General, initialLicenseClass: LicenseClass.Technician);
        var client = new FakeUlsLookupClient(new()
        {
            // Still holds Technician from before the exam — no grant yet, but the license exists.
            ["0038704029"] = ActiveLicense(
                grantDate: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                effectiveDate: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                operatorClass: LicenseClass.Technician)
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesMarkedGranted);
        var updated = await dbContext.Candidates.SingleAsync();
        Assert.Equal(CandidateApplicationStatus.Unmatched, updated.ApplicationStatus);
        Assert.Equal("5339614", updated.FccUlsLicenseKey);   // captured despite no grant
        Assert.Null(updated.CallSign);                        // but the call sign is not adopted
    }

    [Fact]
    public async Task NotFound_LeavesCandidateUnchanged_AndIsNotAFailure()
    {
        await using var dbContext = CreateContext();
        await SeedCandidateAsync(dbContext);
        var client = new FakeUlsLookupClient(new() { ["0038704029"] = UlsLookupResult.NotFound });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.LookupFailures);
        Assert.Equal(CandidateApplicationStatus.Unmatched, (await dbContext.Candidates.SingleAsync()).ApplicationStatus);
    }
}
