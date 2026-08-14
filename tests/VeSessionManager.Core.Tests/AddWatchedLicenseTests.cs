using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>LicenseWatchService.AddWatchedLicenseAsync</c>, moved out of
/// <c>RenewalMonitorModel.OnPostAddAsync</c> in issue #310.
///
/// <para><b>These are the first tests this path has ever had.</b> While it lived on a Razor page it
/// was only reachable by rendering the page and posting a form, which nothing in the suite does —
/// 71 lines of lookup, validation, de-duplication, entity construction, audit and two saves, with no
/// coverage. That is the practical argument for the move, separate from the tidiness one.</para>
/// </summary>
public class AddWatchedLicenseTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class StubClient(UlsLookupResult? result) : IUlsLookupClient
    {
        public List<string> Seen { get; } = [];

        public Task<UlsLookupResult?> LookupAsync(string frnOrCallSign, CancellationToken cancellationToken)
        {
            Seen.Add(frnOrCallSign);
            return Task.FromResult(result);
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "T", ExamToolsTeamCode = "T" };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(new User { Id = 7, UserName = "u", Name = "U", Role = UserRole.TeamAdmin });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static LicenseWatchService Service(AppDbContext dbContext, IUlsLookupClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<LicenseWatchService>.Instance);

    private static UlsLookupResult Found(string callSign) => new()
    {
        Found = true,
        LicenseStatus = "Active",
        CallSign = callSign,
        LicenseeName = "A Licensee",
        OperatorClass = LicenseClass.General,
        GrantDateUtc = new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ExpiredDateUtc = new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task ValidCallSign_CreatesARowAlreadyPopulatedFromTheLookup()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await Service(dbContext, new StubClient(Found("KC1ZYU")))
            .AddWatchedLicenseAsync(team.Id, "kc1zyu", "  club member  ", 7, CancellationToken.None);

        Assert.Equal(AddWatchedLicenseOutcome.Success, result.Outcome);
        Assert.Equal("KC1ZYU", result.CallSign);

        var stored = await dbContext.WatchedLicenses.AsNoTracking().SingleAsync();
        Assert.Equal("KC1ZYU", stored.CallSign);          // normalized, not as typed
        Assert.Equal("club member", stored.Note);          // trimmed
        Assert.Equal(7, stored.AddedByUserId);
        Assert.Equal(Now, stored.AddedUtc);

        // The point of resolving before insert: the row is complete on first render rather than
        // reading "not checked yet" until the Worker's next tick.
        Assert.Equal(Now, stored.LastCheckedUtc);
        Assert.Equal("A Licensee", stored.LicenseeName);
        Assert.Equal(LicenseClass.General, stored.OperatorClass);
        Assert.Equal(new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc), stored.ExpiredDateUtc);
    }

    [Fact]
    public async Task Success_WritesAnAuditEntryCarryingTheNewRowsId()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await Service(dbContext, new StubClient(Found("KC1ZYU")))
            .AddWatchedLicenseAsync(team.Id, "KC1ZYU", null, 7, CancellationToken.None);

        var audit = await dbContext.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("Create", audit.Action);
        Assert.Equal(nameof(WatchedLicense), audit.EntityType);
        // Non-zero is the assertion that matters: the audit is written after the save precisely
        // because the row has no id before it.
        Assert.Equal(result.License!.Id, audit.EntityId);
        Assert.NotEqual(0, audit.EntityId);
    }

    [Fact]
    public async Task BlankEntry_IsRejectedWithoutCallingTheMirror()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var client = new StubClient(Found("KC1ZYU"));

        var result = await Service(dbContext, client)
            .AddWatchedLicenseAsync(team.Id, "   ", null, 7, CancellationToken.None);

        Assert.Equal(AddWatchedLicenseOutcome.CallSignRequired, result.Outcome);
        Assert.Empty(client.Seen);
        Assert.Empty(await dbContext.WatchedLicenses.ToListAsync());
    }

    /// <summary>
    /// The distinction worth keeping: a null lookup means the mirror was unreachable, not that the
    /// call sign is wrong. Collapsing the two would tell someone with a perfectly good call sign
    /// that FCC has never heard of them.
    /// </summary>
    [Fact]
    public async Task UnreachableMirror_IsDistinctFromNotFound()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var unreachable = await Service(dbContext, new StubClient(null))
            .AddWatchedLicenseAsync(team.Id, "kc1zyu", null, 7, CancellationToken.None);
        Assert.Equal(AddWatchedLicenseOutcome.LookupUnavailable, unreachable.Outcome);
        Assert.Equal("KC1ZYU", unreachable.CallSign); // echoed back upper-cased for the message

        var notFound = await Service(dbContext, new StubClient(UlsLookupResult.NotFound))
            .AddWatchedLicenseAsync(team.Id, "nope1", null, 7, CancellationToken.None);
        Assert.Equal(AddWatchedLicenseOutcome.NotFoundAtFcc, notFound.Outcome);
        Assert.Equal("nope1", notFound.CallSign); // as typed, so the message can echo it

        Assert.Empty(await dbContext.WatchedLicenses.ToListAsync());
    }

    [Fact]
    public async Task AlreadyOnThisTeamsList_IsRejected_EvenWhenTypedDifferently()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await Service(dbContext, new StubClient(Found("KC1ZYU")))
            .AddWatchedLicenseAsync(team.Id, "KC1ZYU", null, 7, CancellationToken.None);

        // Lower case this time. The duplicate check compares normalized values, so it must still
        // collide — SQLite's `=` is case-sensitive, which is why the normalization matters.
        var second = await Service(dbContext, new StubClient(Found("kc1zyu")))
            .AddWatchedLicenseAsync(team.Id, "kc1zyu", null, 7, CancellationToken.None);

        Assert.Equal(AddWatchedLicenseOutcome.AlreadyWatched, second.Outcome);
        Assert.Single(await dbContext.WatchedLicenses.ToListAsync());
    }

    /// <summary>Watching is per team, so another team already watching a call sign is not a conflict.</summary>
    [Fact]
    public async Task SameCallSignOnADifferentTeam_IsAllowed()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext);
        var teamB = new Team { Name = "B", ExamToolsTeamCode = "B" };
        dbContext.Teams.Add(teamB);
        await dbContext.SaveChangesAsync();

        await Service(dbContext, new StubClient(Found("KC1ZYU")))
            .AddWatchedLicenseAsync(teamA.Id, "KC1ZYU", null, 7, CancellationToken.None);
        var second = await Service(dbContext, new StubClient(Found("KC1ZYU")))
            .AddWatchedLicenseAsync(teamB.Id, "KC1ZYU", null, 7, CancellationToken.None);

        Assert.Equal(AddWatchedLicenseOutcome.Success, second.Outcome);
        Assert.Equal(2, await dbContext.WatchedLicenses.CountAsync());
    }
}
