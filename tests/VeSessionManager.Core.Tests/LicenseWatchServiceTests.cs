using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Covers the licence watch list — see docs/renewal-monitor.md. Follows UlsWatcherServiceTests' shape:
/// EF InMemory plus a fake lookup client, no live calls.
/// </summary>
public class LicenseWatchServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    // The repo's own fixture rather than Microsoft.Extensions.TimeProvider.Testing — no new
    // package, and it matches CandidateActionServiceTests and friends.
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

    private static LicenseWatchService CreateService(AppDbContext dbContext, IUlsLookupClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<LicenseWatchService>.Instance);

    private static async Task<WatchedLicense> SeedAsync(AppDbContext dbContext, Action<WatchedLicense>? configure = null)
    {
        var team = new Team { Name = "Test Team", ExamToolsTeamCode = "TEST" };
        var licence = new WatchedLicense { Team = team, CallSign = "W1AW", AddedUtc = Now.AddDays(-1) };
        configure?.Invoke(licence);
        dbContext.WatchedLicenses.Add(licence);
        await dbContext.SaveChangesAsync();
        return licence;
    }

    private static UlsLookupResult Found(DateTime? expires, params UlsPendingApplication[] pending) => new()
    {
        Found = true,
        CallSign = "W1AW",
        Frn = "0004511143",
        LicenseeName = "Test Licensee",
        LicenseStatus = "Active",
        OperatorClass = LicenseClass.Extra,
        GrantDateUtc = Now.AddYears(-4),
        ExpiredDateUtc = expires,
        PendingApplications = pending
    };

    // ---- Refresh basics -------------------------------------------------------------------------

    [Fact]
    public async Task NeverCheckedLicence_IsRefreshedAndPopulatedFromTheLookup()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var expires = Now.AddYears(5);
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(expires) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(Now, licence.LastCheckedUtc);
        Assert.Equal(expires, licence.ExpiredDateUtc);
        Assert.Equal("Test Licensee", licence.LicenseeName);
        // A row added by call sign acquires its FRN from the lookup.
        Assert.Equal("0004511143", licence.Frn);
    }

    [Fact]
    public async Task LookupIsByCallSign_NotFrn()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext, l => l.Frn = "0004511143");
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(Now.AddYears(5)) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(["W1AW"], client.LookedUp);
    }

    [Fact]
    public async Task FreshlyCheckedLicence_IsNotLookedUpAgain()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext, l => l.LastCheckedUtc = Now.AddHours(-1));
        var client = new FakeUlsLookupClient([]);

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Due);
        Assert.Empty(client.LookedUp);
    }

    /// <summary>
    /// A failed lookup must leave LastCheckedUtc alone. Stamping it would mark the row fresh and
    /// silently park it for a full refresh interval on the strength of an error.
    /// </summary>
    [Fact]
    public async Task FailedLookup_LeavesTheRowStaleSoItRetries()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = null });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Null(licence.LastCheckedUtc);
        Assert.Equal(1, result.LookupFailures);
    }

    [Fact]
    public async Task NotFoundAtFcc_IsFlaggedRatherThanTreatedAsAFailure()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = UlsLookupResult.NotFound });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.True(licence.NotFoundAtFcc);
        Assert.Equal(Now, licence.LastCheckedUtc);
        Assert.Equal(0, result.LookupFailures);
        Assert.Equal(WatchedLicenseStatus.NotFound, licence.DeriveStatus(Now));
    }

    // ---- Renewal lifecycle ----------------------------------------------------------------------

    private static UlsPendingApplication Renewal(string fileNumber = "0012131564") =>
        new() { UlsFileNumber = fileNumber, ApplicationPurpose = "RO", ReceiptDateUtc = Now.AddDays(-2) };

    [Fact]
    public async Task PendingRenewal_IsDetectedAndAnchoredToTheCurrentExpiry()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var expires = Now.AddDays(40);
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(expires, Renewal()) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(Now, licence.RenewalPendingSinceUtc);
        Assert.Equal("0012131564", licence.RenewalFileNumber);
        // The anchor is what the renewal must beat before it can be called issued.
        Assert.Equal(expires, licence.ExpiredDateWhenRenewalFiledUtc);
        Assert.Equal(1, result.RenewalsDetected);
        Assert.Equal(WatchedLicenseStatus.RenewalPending, licence.DeriveStatus(Now));
    }

    /// <summary>RenewalPendingSinceUtc records when *we* first saw it and must not creep forward on every poll — otherwise "pending since" is always today and the wait is invisible.</summary>
    [Fact]
    public async Task StillPendingRenewal_DoesNotResetWhenItWasFirstSeen()
    {
        using var dbContext = CreateContext();
        var firstSeen = Now.AddDays(-10);
        await SeedAsync(dbContext, l =>
        {
            l.RenewalPendingSinceUtc = firstSeen;
            l.RenewalFileNumber = "0012131564";
            l.ExpiredDateWhenRenewalFiledUtc = Now.AddDays(40);
        });
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(Now.AddDays(40), Renewal()) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(firstSeen, licence.RenewalPendingSinceUtc);
    }

    [Fact]
    public async Task RenewalIssued_WhenTheExpiryAdvancesPastTheAnchor()
    {
        using var dbContext = CreateContext();
        var oldExpiry = Now.AddDays(40);
        await SeedAsync(dbContext, l =>
        {
            l.RenewalPendingSinceUtc = Now.AddDays(-10);
            l.RenewalFileNumber = "0012131564";
            l.ExpiredDateWhenRenewalFiledUtc = oldExpiry;
            l.ExpiredDateUtc = oldExpiry;
        });
        // FCC granted the new ten-year term and has already dropped the application.
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(oldExpiry.AddYears(10)) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(Now, licence.RenewalConfirmedUtc);
        Assert.Null(licence.RenewalPendingSinceUtc);
        Assert.Null(licence.ExpiredDateWhenRenewalFiledUtc);
        Assert.Equal(1, result.RenewalsConfirmed);
        Assert.Equal(WatchedLicenseStatus.Renewed, licence.DeriveStatus(Now));
    }

    /// <summary>
    /// FCC can leave the application listed for a while after granting the new term. Confirmation is
    /// therefore tested before the still-pending branch — otherwise the row sticks on "renewal
    /// pending" until FCC tidies up, long after the renewal actually landed.
    /// </summary>
    [Fact]
    public async Task RenewalIssued_EvenWhileTheApplicationIsStillListedAsPending()
    {
        using var dbContext = CreateContext();
        var oldExpiry = Now.AddDays(40);
        await SeedAsync(dbContext, l =>
        {
            l.RenewalPendingSinceUtc = Now.AddDays(-10);
            l.ExpiredDateWhenRenewalFiledUtc = oldExpiry;
            l.ExpiredDateUtc = oldExpiry;
        });
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(oldExpiry.AddYears(10), Renewal()) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(WatchedLicenseStatus.Renewed, licence.DeriveStatus(Now));
    }

    /// <summary>
    /// The real KA0MVW case, 2026-08-06: the renewal was granted between two polls, so this app never
    /// saw the application pending beforehand. It previously recorded that as newly *pending*,
    /// anchored against the already-updated expiry — which could then never be beaten, so the row sat
    /// on "Renewal pending" until FCC dropped the application and fell through to plain Active,
    /// never once reporting the renewal it had just watched land.
    /// </summary>
    [Fact]
    public async Task RenewalGrantedBetweenPolls_IsConfirmed_NotReportedAsPending()
    {
        using var dbContext = CreateContext();
        var oldExpiry = Now.AddDays(1);
        await SeedAsync(dbContext, l =>
        {
            l.ExpiredDateUtc = oldExpiry;       // what we had stored
            l.LastCheckedUtc = Now.AddDays(-1); // and never saw pending
        });

        // FCC has already granted it, and still lists the application.
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(oldExpiry.AddYears(10), Renewal()) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(WatchedLicenseStatus.Renewed, licence.DeriveStatus(Now));
        Assert.Equal(Now, licence.RenewalConfirmedUtc);
        Assert.Null(licence.RenewalPendingSinceUtc);
        Assert.Equal(1, result.RenewalsConfirmed);
    }

    /// <summary>Even with no application listed at all — FCC having already tidied it away — an expiry that advanced is still a renewal.</summary>
    [Fact]
    public async Task ExpiryAdvancingWithNoApplicationListed_IsStillConfirmed()
    {
        using var dbContext = CreateContext();
        var oldExpiry = Now.AddDays(1);
        await SeedAsync(dbContext, l => l.ExpiredDateUtc = oldExpiry);
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(oldExpiry.AddYears(10)) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(WatchedLicenseStatus.Renewed, (await dbContext.WatchedLicenses.SingleAsync()).DeriveStatus(Now));
    }

    /// <summary>A first-ever check has nothing to compare against, so a pending application is still just pending — not a phantom renewal.</summary>
    [Fact]
    public async Task FirstEverCheck_WithAPendingApplication_IsPendingNotConfirmed()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);  // never checked, no stored expiry
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(Now.AddDays(40), Renewal()) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(WatchedLicenseStatus.RenewalPending, licence.DeriveStatus(Now));
        Assert.Null(licence.RenewalConfirmedUtc);
    }

    [Fact]
    public async Task RenewalThatDisappearsWithoutTheExpiryMoving_IsTreatedAsAbandoned()
    {
        using var dbContext = CreateContext();
        var expiry = Now.AddDays(40);
        await SeedAsync(dbContext, l =>
        {
            l.RenewalPendingSinceUtc = Now.AddDays(-10);
            l.ExpiredDateWhenRenewalFiledUtc = expiry;
            l.ExpiredDateUtc = expiry;
        });
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(expiry) });

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Null(licence.RenewalPendingSinceUtc);
        Assert.Null(licence.RenewalConfirmedUtc);
        Assert.Equal(1, result.RenewalsAbandoned);
        // Back to reporting the real expiry rather than a stale "pending".
        Assert.Equal(WatchedLicenseStatus.ExpiringSoon, licence.DeriveStatus(Now));
    }

    /// <summary>A non-renewal application (a modification, say) must not start the renewal clock.</summary>
    [Fact]
    public async Task NonRenewalApplication_IsIgnored()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var modification = new UlsPendingApplication { UlsFileNumber = "1", ApplicationPurpose = "MD" };
        var client = new FakeUlsLookupClient(new() { ["W1AW"] = Found(Now.AddYears(5), modification) });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Null(licence.RenewalPendingSinceUtc);
    }

    /// <summary>An unrecognised purpose code degrades to "not a renewal" rather than throwing — the code list is FCC's documented one but has not been seen live.</summary>
    [Theory]
    // Codes, kept for other endpoints / future shape changes.
    [InlineData("RO", true)]
    [InlineData("rm", true)]
    [InlineData(" RO ", true)]
    // Descriptions — what ExamTools actually returns. "Renewal/Modification" is a REAL value,
    // observed live on 2026-08-06 for KA0MVW; the original code-only matcher scored it false and
    // silently disabled renewal detection entirely.
    [InlineData("Renewal/Modification", true)]
    [InlineData("Renewal Only", true)]
    [InlineData("renewal/modification", true)]
    [InlineData("NE", false)]
    [InlineData("New", false)]
    [InlineData("Modification", false)]
    [InlineData("Administrative Update", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RenewalPurposeCodes_AreMatchedLenientlyAndSafely(string? purpose, bool expected)
    {
        var application = new UlsPendingApplication { ApplicationPurpose = purpose };
        Assert.Equal(expected, application.IsRenewal);
    }

    // ---- Status derivation ----------------------------------------------------------------------

    [Fact]
    public void ExpiryThresholds_MatchFccsRenewalWindowAndGracePeriod()
    {
        WatchedLicense At(DateTime expires) => new()
        {
            CallSign = "W1AW",
            LastCheckedUtc = Now,
            ExpiredDateUtc = expires
        };

        Assert.Equal(WatchedLicenseStatus.Active, At(Now.AddDays(120)).DeriveStatus(Now));
        Assert.Equal(WatchedLicenseStatus.ExpiringSoon, At(Now.AddDays(89)).DeriveStatus(Now));
        Assert.Equal(WatchedLicenseStatus.ExpiredInGrace, At(Now.AddDays(-1)).DeriveStatus(Now));
        Assert.Equal(WatchedLicenseStatus.ExpiredInGrace, At(Now.AddDays(-729)).DeriveStatus(Now));
        Assert.Equal(WatchedLicenseStatus.ExpiredLapsed, At(Now.AddDays(-731)).DeriveStatus(Now));
    }

    /// <summary>
    /// A cancelled record keeps whatever expiration it had, so testing dates before cancellation
    /// would report a revoked licence as comfortably Active.
    /// </summary>
    [Fact]
    public void Cancellation_OutranksAFutureExpiry()
    {
        var licence = new WatchedLicense
        {
            CallSign = "W1AW",
            LastCheckedUtc = Now,
            ExpiredDateUtc = Now.AddYears(5),
            CancellationDateUtc = Now.AddDays(-3)
        };

        Assert.Equal(WatchedLicenseStatus.Cancelled, licence.DeriveStatus(Now));
    }

    [Fact]
    public void NeverChecked_IsDistinguishableFromNotFound()
    {
        var neverChecked = new WatchedLicense { CallSign = "W1AW" };
        Assert.Equal(WatchedLicenseStatus.NotYetChecked, neverChecked.DeriveStatus(Now));
    }

    /// <summary>A found record with no expiration date says nothing about its term — don't invent an alarming answer.</summary>
    [Fact]
    public void FoundRecordWithNoExpiry_IsActiveNotExpired()
    {
        var licence = new WatchedLicense { CallSign = "W1AW", LastCheckedUtc = Now };
        Assert.Equal(WatchedLicenseStatus.Active, licence.DeriveStatus(Now));
    }

    /// <summary>
    /// A real ULS record, read live on 2026-08-05 (KA0MVW): Active, General, granted 2016-07-01,
    /// expiring 2026-08-07, no pending applications. Two days out with nothing filed is exactly the
    /// case this feature exists to surface, so it is pinned rather than left to the synthetic
    /// threshold tests above. Call sign only — the response's name and address are not stored by the
    /// app and have no business in a fixture.
    /// </summary>
    [Fact]
    public async Task RealRecord_ExpiringInTwoDaysWithNoRenewal_ReportsExpiringSoon()
    {
        using var dbContext = CreateContext();
        await SeedAsync(dbContext, l => l.CallSign = "KA0MVW");

        var live = new UlsLookupResult
        {
            Found = true,
            CallSign = "KA0MVW",
            Frn = "0004717963",
            LicenseStatus = "Active",
            OperatorClass = LicenseClass.General,
            // "Technician Plus" is a legacy spelling the class parser has to accept.
            PreviousOperatorClass = LicenseClass.Technician,
            GrantDateUtc = new DateTime(2016, 7, 1, 8, 0, 0, DateTimeKind.Utc),
            EffectiveDateUtc = new DateTime(2016, 7, 1, 8, 0, 0, DateTimeKind.Utc),
            ExpiredDateUtc = new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc),
            PendingApplications = []
        };
        var client = new FakeUlsLookupClient(new() { ["KA0MVW"] = live });

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var licence = await dbContext.WatchedLicenses.SingleAsync();
        Assert.Equal(WatchedLicenseStatus.ExpiringSoon, licence.DeriveStatus(Now));
        Assert.True(licence.DeriveStatus(Now).NeedsAttention());
        Assert.Null(licence.RenewalPendingSinceUtc);
        // Still inside the window, so it is renewable today without re-testing.
        Assert.True(licence.ExpiredDateUtc > Now);
    }

    /// <summary>
    /// Reported from the live page: KA0MVW expires 7 Aug, the page was viewed on 5 Aug, and the pill
    /// read "1 d". The original arithmetic subtracted instants and floored, measuring elapsed time to
    /// the *start* of the expiry date — at 05:00 UTC on the 5th that is 1.78 days. Calendar-day
    /// counting is what a human means.
    /// </summary>
    [Theory]
    [InlineData("2026-08-05T05:00:00Z", 2)]  // early morning UTC, the reported case
    [InlineData("2026-08-05T12:00:00Z", 2)]  // midday
    [InlineData("2026-08-05T23:30:00Z", 2)]  // 7:30pm ET — still the 5th locally, though UTC agrees
    [InlineData("2026-08-06T01:00:00Z", 2)]  // 9pm ET on the 5th: UTC has rolled over, Eastern has not
    [InlineData("2026-08-07T12:00:00Z", 0)]  // expires today
    [InlineData("2026-08-08T12:00:00Z", -1)] // expired yesterday
    public void DaysUntilExpiry_CountsCalendarDaysInEastern(string nowIso, int expected)
    {
        var licence = new WatchedLicense
        {
            CallSign = "KA0MVW",
            LastCheckedUtc = Now,
            ExpiredDateUtc = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc)
        };

        var utcNow = DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        Assert.Equal(expected, licence.DaysUntilExpiry(utcNow));
    }

    /// <summary>
    /// A licence is valid THROUGH its expiration date. Comparing raw instants flipped it to Expired
    /// at midnight on that date — a full day early, and visible to the user as a red "Expired" chip
    /// on a licence they could still legally operate.
    /// </summary>
    [Fact]
    public void ExpiresToday_IsStillCurrent_NotExpired()
    {
        var licence = new WatchedLicense
        {
            CallSign = "KA0MVW",
            LastCheckedUtc = Now,
            ExpiredDateUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        };

        // Now is midday UTC on 5 Aug — the expiry date itself.
        Assert.Equal(WatchedLicenseStatus.ExpiringSoon, licence.DeriveStatus(Now));
        Assert.Equal(0, licence.DaysUntilExpiry(Now));
    }

    [Fact]
    public void RenewedHighlight_FadesBackToActive()
    {
        WatchedLicense Renewed(DateTime confirmed) => new()
        {
            CallSign = "W1AW",
            LastCheckedUtc = Now,
            ExpiredDateUtc = Now.AddYears(10),
            RenewalConfirmedUtc = confirmed
        };

        Assert.Equal(WatchedLicenseStatus.Renewed, Renewed(Now.AddDays(-5)).DeriveStatus(Now));
        Assert.Equal(WatchedLicenseStatus.Active, Renewed(Now.AddDays(-40)).DeriveStatus(Now));
    }
}
