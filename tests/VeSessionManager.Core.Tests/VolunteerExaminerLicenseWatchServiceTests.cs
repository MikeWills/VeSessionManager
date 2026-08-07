using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE license sweep (issue #107). Follows LicenseWatchServiceTests' shape: EF InMemory plus a
/// fake lookup client, no live calls.
/// </summary>
public class VolunteerExaminerLicenseWatchServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeUlsLookupClient(Dictionary<string, UlsLookupResult?> byKey) : IUlsLookupClient
    {
        public List<string> LookedUp { get; } = [];

        public Task<UlsLookupResult?> LookupByFrnAsync(string frnOrCallSign, CancellationToken cancellationToken)
        {
            LookedUp.Add(frnOrCallSign);
            return Task.FromResult(byKey.TryGetValue(frnOrCallSign, out var r) ? r : UlsLookupResult.NotFound);
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerLicenseWatchService CreateService(AppDbContext dbContext, IUlsLookupClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<VolunteerExaminerLicenseWatchService>.Instance);

    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, string? callSign, bool activeMembership = true, DateTime? lastChecked = null)
    {
        var team = new Team { Name = $"TEAM-{Guid.NewGuid()}", ExamToolsTeamCode = "T", CreatedUtc = Now };
        var person = new VolunteerExaminer
        {
            Name = "Sam Granger",
            CallSign = callSign,
            CreatedUtc = Now.AddYears(-1),
            LicenseLastCheckedUtc = lastChecked
        };
        dbContext.VeTeamMemberships.Add(new VeTeamMembership
        {
            VolunteerExaminer = person,
            Team = team,
            IsActive = activeMembership,
            CreatedUtc = Now
        });
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    private static UlsLookupResult Found(
        DateTime? expires, string callSign = "N2SPG", string? frn = "0004511143", LicenseClass operatorClass = LicenseClass.Extra) => new()
    {
        Found = true,
        CallSign = callSign,
        Frn = frn,
        LicenseStatus = "Active",
        OperatorClass = operatorClass,
        GrantDateUtc = Now.AddYears(-4),
        ExpiredDateUtc = expires
    };

    [Fact]
    public async Task ActiveVe_IsCheckedAndPopulated()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        var expires = Now.AddYears(5);
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = Found(expires) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal(Now, person.LicenseLastCheckedUtc);
        Assert.Equal(expires, person.LicenseExpiresUtc);
        Assert.Equal(LicenseClass.Extra, person.OperatorClass);
        Assert.Equal(1, result.Checked);
        Assert.Equal(WatchedLicenseStatus.Active, person.DeriveSnapshotStatus(Now));
    }

    /// <summary>
    /// The sweep is what finally gives a VE an FRN — ExamTools' roster never reports one, and FRN is
    /// the only identifier that survives a vanity call sign change (issue #142's identity model).
    /// </summary>
    [Fact]
    public async Task LookupBackfillsTheFrn()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = Found(Now.AddYears(5)) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal("0004511143", (await dbContext.VolunteerExaminers.SingleAsync()).Frn);
        Assert.Equal(1, result.FrnsBackfilled);
    }

    /// <summary>A vanity call sign coming through: follow FCC, and keep the old one so a roster still naming them by it resolves to this person rather than minting a second.</summary>
    [Fact]
    public async Task CallSignChange_IsFollowedAndTheOldOneKept()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "KF0JZP");
        var client = new FakeUlsLookupClient(new() { ["KF0JZP"] = Found(Now.AddYears(5), callSign: "W1XYZ") });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var person = await dbContext.VolunteerExaminers.Include(v => v.CallSignHistory).SingleAsync();
        Assert.Equal("W1XYZ", person.CallSign);
        Assert.Equal(1, result.CallSignsChanged);
        var history = Assert.Single(person.CallSignHistory);
        Assert.Equal("KF0JZP", history.CallSign);
        Assert.Equal(Now, history.ReplacedUtc);
    }

    /// <summary>
    /// ExamTools' "&lt;UNKNOWN&gt;" placeholder has nothing to look up. Checking it would burn a call
    /// per run forever and come back not-found every time, which reads as a real FCC answer.
    /// </summary>
    [Fact]
    public async Task VeWithAnUnusableCallSign_IsSkippedNotLookedUp()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "<UNKNOWN>");
        var client = new FakeUlsLookupClient([]);

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Empty(client.LookedUp);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Checked);

        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Null(person.LicenseLastCheckedUtc);
        Assert.Equal(WatchedLicenseStatus.NoCallSign, person.DeriveSnapshotStatus(Now));
    }

    /// <summary>A VE retired from every team they served is never going to be assigned to a session, so their license is nobody's question — and the sweep must not grow forever as teams turn over.</summary>
    [Fact]
    public async Task VeRetiredFromEveryTeam_IsNotChecked()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG", activeMembership: false);
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = Found(Now.AddYears(5)) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Empty(client.LookedUp);
    }

    [Fact]
    public async Task FreshlyCheckedVe_IsNotLookedUpAgain()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG", lastChecked: Now.AddHours(-1));
        var client = new FakeUlsLookupClient([]);

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Due);
        Assert.Empty(client.LookedUp);
    }

    /// <summary>A failed lookup must leave the row stale so the next run retries, rather than parking it for a full interval on the strength of an error.</summary>
    [Fact]
    public async Task FailedLookup_LeavesTheRowStale()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = null });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.LookupFailures);
        Assert.Null((await dbContext.VolunteerExaminers.SingleAsync()).LicenseLastCheckedUtc);
    }

    [Fact]
    public async Task ExpiredLicense_DerivesThroughTheSharedRules()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = Found(Now.AddDays(-30)) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal(WatchedLicenseStatus.ExpiredInGrace, person.DeriveSnapshotStatus(Now));
    }

    /// <summary>
    /// Found on the first real run. Two VE rows resolving to one FRN is <i>proof</i> they are the
    /// same human — stronger than a shared call sign, which can be a reissue — and it happens for
    /// exactly the rows the #142 merge left alone because their names disagreed. Writing the second
    /// one would violate the unique index; stealing it from the first would be worse, since merging
    /// people is not reversible. So it is recorded and skipped.
    /// </summary>
    [Fact]
    public async Task TwoVesResolvingToOneFrn_AreRecordedAsAConflictNotWritten()
    {
        await using var dbContext = CreateContext();
        var first = await SeedVeAsync(dbContext, "N2SPG");
        var second = await SeedVeAsync(dbContext, "KF0JZP");
        var client = new FakeUlsLookupClient(new()
        {
            ["N2SPG"] = Found(Now.AddYears(4), callSign: "N2SPG", frn: "0004511143"),
            ["KF0JZP"] = Found(Now.AddYears(4), callSign: "KF0JZP", frn: "0004511143")
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.FrnsBackfilled);
        Assert.Equal(1, result.FrnConflicts);
        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Checked);

        // Exactly one row holds the FRN; the other is left alone rather than having it stolen.
        var withFrn = await dbContext.VolunteerExaminers.Where(v => v.Frn == "0004511143").ToListAsync();
        Assert.Single(withFrn);

        // And the pair is identifiable, not just counted.
        var (conflicted, owner, frn) = Assert.Single(result.ConflictingFrnOwnerIds);
        Assert.Equal("0004511143", frn);
        Assert.Contains(owner, new[] { first.Id, second.Id });
        Assert.Contains(conflicted, new[] { first.Id, second.Id });
        Assert.NotEqual(owner, conflicted);

        // Both still got their license state — a conflict is not a reason to skip the whole row.
        Assert.All(await dbContext.VolunteerExaminers.ToListAsync(), v => Assert.NotNull(v.LicenseLastCheckedUtc));
    }

    /// <summary>
    /// One bad row must not end the sweep. Before the per-row guard, the FRN collision above threw
    /// out of SaveChangesAsync and every later VE went unchecked — on the first real run, that was
    /// most of the roster.
    /// </summary>
    [Fact]
    public async Task OneFailingRow_DoesNotStopTheRest()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        await SeedVeAsync(dbContext, "NP2UU");
        await SeedVeAsync(dbContext, "W1AW");

        var client = new FakeUlsLookupClient(new()
        {
            ["N2SPG"] = Found(Now.AddYears(4), callSign: "N2SPG", frn: "1"),
            ["NP2UU"] = null,   // lookup failure
            ["W1AW"] = Found(Now.AddYears(4), callSign: "W1AW", frn: "3")
        });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.LookupFailures);
        Assert.Equal(3, client.LookedUp.Count);   // it kept going
    }

    /// <summary>The 90-day threshold is the shared one — this is the test that would fail if a second copy of it ever appeared.</summary>
    [Fact]
    public async Task LicenseInsideTheRenewalWindow_IsExpiringSoon()
    {
        await using var dbContext = CreateContext();
        await SeedVeAsync(dbContext, "N2SPG");
        var client = new FakeUlsLookupClient(new() { ["N2SPG"] = Found(Now.AddDays(30)) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(WatchedLicenseStatus.ExpiringSoon,
            (await dbContext.VolunteerExaminers.SingleAsync()).DeriveSnapshotStatus(Now));
    }
}
