using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="VeLicenseStatusFilter"/> is a second statement of a rule that already exists in
/// <c>DeriveSnapshotStatus</c> — one for the database, one for C#. That is the duplication this
/// codebase has been bitten by before (DUP-02, the session-completion rule written nine times), and
/// the only thing that makes it safe is a test that fails the moment the two disagree.
///
/// <para><b>Real SQLite, necessarily.</b> The filter uses <c>GLOB</c> for the call-sign shape test,
/// which EF InMemory cannot run at all — and more fundamentally, a rule written to be translated is
/// only meaningfully tested against a provider that translates it.</para>
/// </summary>
public class VeLicenseStatusFilterSqliteTests
{
    /// <summary>Mid-August, so the ±90-day and ±730-day thresholds land unambiguously inside a year.</summary>
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    /// <summary>
    /// One VE per interesting shape. Deliberately includes the awkward call signs — ExamTools'
    /// literal placeholder, a word with no digit, and a valid portable call sign with a '/' — because
    /// the character-class half of the rule is the part most likely to translate differently.
    /// </summary>
    private static List<VolunteerExaminer> Matrix()
    {
        var today = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);

        VolunteerExaminer Ve(string name, string? callSign, bool notFound = false,
            DateTime? checkedUtc = null, DateTime? cancelled = null, DateTime? expires = null) =>
            new()
            {
                Name = name,
                CallSign = callSign,
                LicenseNotFoundAtFcc = notFound,
                LicenseLastCheckedUtc = checkedUtc,
                LicenseCancellationDateUtc = cancelled,
                LicenseExpiresUtc = expires,
                CreatedUtc = today
            };

        var checkedNow = today.AddDays(-1);

        return
        [
            // --- the call-sign shape half ---
            Ve("null call sign", null),
            Ve("empty call sign", ""),
            Ve("whitespace call sign", "   "),
            Ve("examtools placeholder", "<UNKNOWN>"),
            Ve("word, no digit", "UNKNOWN"),
            Ve("digits, no letter", "12345"),
            Ve("punctuation", "N/A!"),
            Ve("portable, valid", "N2SPG/M", checkedUtc: checkedNow, expires: today.AddYears(5)),

            // --- the cascade, in order ---
            Ve("not found", "K0AAA", notFound: true),
            Ve("never checked", "K0BBB"),
            Ve("cancelled", "K0CCC", checkedUtc: checkedNow, cancelled: today.AddDays(-30), expires: today.AddYears(5)),

            // Cancellation outranks a comfortable future expiry — the ordering that would silently
            // report a revoked licence as Active if the date tests came first.
            Ve("cancelled but not yet expired", "K0DDD", checkedUtc: checkedNow, cancelled: today.AddDays(-1), expires: today.AddYears(9)),

            Ve("no expiry recorded", "K0EEE", checkedUtc: checkedNow),

            // --- the date thresholds, on both sides of each boundary ---
            Ve("lapsed, well past grace", "K0FFF", checkedUtc: checkedNow, expires: today.AddDays(-731)),
            Ve("exactly at grace edge", "K0GGG", checkedUtc: checkedNow, expires: today.AddDays(-730)),
            Ve("in grace", "K0HHH", checkedUtc: checkedNow, expires: today.AddDays(-1)),
            Ve("expires today", "K0III", checkedUtc: checkedNow, expires: today),
            Ve("expiring soon, inside window", "K0JJJ", checkedUtc: checkedNow, expires: today.AddDays(89)),
            Ve("expiring exactly on the window", "K0KKK", checkedUtc: checkedNow, expires: today.AddDays(90)),
            Ve("comfortably active", "K0LLL", checkedUtc: checkedNow, expires: today.AddDays(91))
        ];
    }

    /// <summary>
    /// The guard. For every value of the enum, the set the database selects must equal the set C#
    /// would select — compared by name, so a failure says which VE and which status rather than just
    /// a count.
    /// </summary>
    [Fact]
    public async Task SqlAndCSharpAgreeForEveryStatus()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.AddRange(Matrix());
        await dbContext.SaveChangesAsync();

        var all = await dbContext.VolunteerExaminers.AsNoTracking().ToListAsync();

        foreach (var status in Enum.GetValues<WatchedLicenseStatus>())
        {
            var fromSql = await dbContext.VolunteerExaminers
                .AsNoTracking()
                .Where(VeLicenseStatusFilter.For(status, Now))
                .Select(v => v.Name)
                .OrderBy(n => n)
                .ToListAsync();

            var fromCSharp = all
                .Where(v => v.DeriveSnapshotStatus(Now) == status)
                .Select(v => v.Name)
                .OrderBy(n => n)
                .ToList();

            Assert.Equal(fromCSharp, fromSql);
        }
    }

    /// <summary>
    /// Every status the snapshot rule can actually produce.
    ///
    /// <para><c>RenewalPending</c> and <c>Renewed</c> are deliberately absent: they belong to
    /// <c>DeriveStatus(WatchedLicense, …)</c>, the Renewal Monitor's fuller rule layered on top of
    /// this one, and <c>DeriveSnapshotStatus</c> can never return them. The enum is shared by both.
    /// So filtering the VE Directory by either yields nothing — which is exactly what it did before
    /// this filter moved into SQL, so it is not a regression, but it is worth knowing rather than
    /// rediscovering from an empty page.</para>
    /// </summary>
    private static readonly WatchedLicenseStatus[] SnapshotProducible =
    [
        WatchedLicenseStatus.NoCallSign,
        WatchedLicenseStatus.NotFound,
        WatchedLicenseStatus.NotYetChecked,
        WatchedLicenseStatus.Cancelled,
        WatchedLicenseStatus.Active,
        WatchedLicenseStatus.ExpiredLapsed,
        WatchedLicenseStatus.ExpiredInGrace,
        WatchedLicenseStatus.ExpiringSoon
    ];

    /// <summary>
    /// The matrix has to actually exercise the rule. Without this, deleting every row would make the
    /// test above pass perfectly — empty equals empty, for every status.
    /// </summary>
    [Fact]
    public async Task EveryProducibleStatusIsRepresentedInTheMatrix()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.AddRange(Matrix());
        await dbContext.SaveChangesAsync();

        var all = await dbContext.VolunteerExaminers.AsNoTracking().ToListAsync();
        var covered = all.Select(v => v.DeriveSnapshotStatus(Now)).Distinct().Order().ToList();

        Assert.Equal(SnapshotProducible.Order().ToList(), covered);
    }

    /// <summary>
    /// And the two the snapshot rule cannot produce select nobody, rather than throwing or — worse —
    /// quietly matching the wrong people because they fell off the end of the cascade.
    /// </summary>
    [Theory]
    [InlineData(WatchedLicenseStatus.RenewalPending)]
    [InlineData(WatchedLicenseStatus.Renewed)]
    public async Task RenewalOnlyStatusesSelectNobody(WatchedLicenseStatus status)
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        dbContext.VolunteerExaminers.AddRange(Matrix());
        await dbContext.SaveChangesAsync();

        Assert.Empty(await dbContext.VolunteerExaminers.Where(VeLicenseStatusFilter.For(status, Now)).ToListAsync());
    }
}
