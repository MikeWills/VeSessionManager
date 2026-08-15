using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Paging the VE directory (#298).
///
/// <para>The directory used to materialize the whole roster and apply three filters in C#
/// afterwards, so it had no paging path at all. Getting one meant making every filter translatable
/// — including "guest" (no tag on <i>any</i> team in scope) and last-worked (a maximum across the
/// teams in scope), which are properties of the grouped row rather than of a membership.</para>
///
/// <para><b>Why paging and filtering have to be tested together.</b> The tempting shortcut is to page
/// on the filters that translate and apply the rest to the page afterwards. That produces pages
/// rendering zero rows while the pager says "showing 1–25 of 176" — worse than the unpaged list. So
/// these tests page <i>with a filter applied</i> and check the page is full and the count honest.</para>
///
/// <para>Real SQLite: the license filter uses GLOB, which InMemory cannot run.</para>
/// </summary>
public class VeDirectoryPagingSqliteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<SqliteConnection> connections = [];

    public void Dispose()
    {
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
    }

    private AppDbContext CreateContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        connections.Add(connection);
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>
    /// <paramref name="index"/> drives the name so ordering is predictable, and the call sign stays
    /// usable so these people are not all classified NoCallSign.
    /// </summary>
    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, Team team, int index, bool tagged = false, VeTag? tag = null)
    {
        var person = new VolunteerExaminer
        {
            Name = $"VE {index:D3}",
            CallSign = $"K0A{index:D3}",
            LicenseLastCheckedUtc = Now.AddDays(-1),
            LicenseExpiresUtc = Now.AddYears(5),
            OperatorClass = LicenseClass.Extra
        };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();

        var membership = new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true };
        dbContext.VeTeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync();

        if (tagged && tag is not null)
        {
            dbContext.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag.Id });
            await dbContext.SaveChangesAsync();
        }

        return person;
    }

    private static VolunteerExaminerDirectoryService Service(AppDbContext dbContext) => new(dbContext);

    [Fact]
    public async Task PageOneIsAFullPageAndTheCountIsTheWholeMatch()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        for (var i = 0; i < 30; i++)
        {
            await SeedVeAsync(dbContext, team, i);
        }

        var page = await Service(dbContext).GetDirectoryPageAsync(
            null, new VeDirectoryFilter(), Now, pageNumber: 1, pageSize: 10, CancellationToken.None);

        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(30, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal("VE 000", page.Rows[0].VolunteerExaminer.Name);
    }

    /// <summary>Every person appears exactly once across the pages — no drops, no repeats.</summary>
    [Fact]
    public async Task PagingVisitsEveryPersonExactlyOnce()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        for (var i = 0; i < 30; i++)
        {
            await SeedVeAsync(dbContext, team, i);
        }

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await Service(dbContext).GetDirectoryPageAsync(
                null, new VeDirectoryFilter(), Now, page, pageSize: 10, CancellationToken.None);
            seen.AddRange(result.Rows.Select(r => r.VolunteerExaminer.Name));
        }

        Assert.Equal(30, seen.Count);
        Assert.Equal(30, seen.Distinct().Count());
    }

    /// <summary>
    /// The failure the shortcut would have produced. With a filter that keeps a third of the roster,
    /// page 1 must be a <b>full</b> page of matches and the count must be the number of matches —
    /// not the number of people scanned to find them.
    /// </summary>
    [Fact]
    public async Task FilteringThenPaging_GivesFullPagesAndAnHonestCount()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var tag = new VeTag { TeamId = team.Id, Name = "Member", SortOrder = 1 };
        dbContext.VeTags.Add(tag);
        await dbContext.SaveChangesAsync();

        // 30 tagged, 60 untagged. Filtering to guests must page over the 60.
        for (var i = 0; i < 30; i++)
        {
            await SeedVeAsync(dbContext, team, i, tagged: true, tag: tag);
        }
        for (var i = 30; i < 90; i++)
        {
            await SeedVeAsync(dbContext, team, i);
        }

        var page = await Service(dbContext).GetDirectoryPageAsync(
            null,
            new VeDirectoryFilter { TagName = VolunteerExaminerDirectoryService.GuestTagFilter },
            Now, pageNumber: 1, pageSize: 25, CancellationToken.None);

        Assert.Equal(25, page.Rows.Count);
        Assert.Equal(60, page.TotalCount);
        Assert.All(page.Rows, r => Assert.True(r.IsGuest));
    }

    /// <summary>A page past the end lands on the last real one rather than rendering nothing.</summary>
    [Fact]
    public async Task APageBeyondTheEndIsClampedToTheLast()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        for (var i = 0; i < 12; i++)
        {
            await SeedVeAsync(dbContext, team, i);
        }

        var page = await Service(dbContext).GetDirectoryPageAsync(
            null, new VeDirectoryFilter(), Now, pageNumber: 99, pageSize: 10, CancellationToken.None);

        Assert.Equal(2, page.PageNumber);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(12, page.TotalCount);
    }

    /// <summary>
    /// The paged and unpaged reads must agree about <i>who</i> matches. This is what stops the CSV
    /// export — which deliberately stays unpaged — from drifting away from the page it was launched
    /// from.
    /// </summary>
    [Fact]
    public async Task ThePagedAndUnpagedReadsSelectTheSamePeople()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        for (var i = 0; i < 25; i++)
        {
            await SeedVeAsync(dbContext, team, i);
        }

        var filter = new VeDirectoryFilter { LicenseStatus = WatchedLicenseStatus.Active };

        var all = await Service(dbContext).GetDirectoryAsync(null, filter, Now, CancellationToken.None);

        var paged = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await Service(dbContext).GetDirectoryPageAsync(
                null, filter, Now, page, pageSize: 10, CancellationToken.None);
            paged.AddRange(result.Rows.Select(r => r.VolunteerExaminer.Name));
        }

        Assert.Equal(25, all.Count);
        Assert.Equal(all.Select(r => r.VolunteerExaminer.Name).ToList(), paged);
    }
}
