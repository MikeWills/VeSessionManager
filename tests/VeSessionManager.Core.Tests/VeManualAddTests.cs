using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Adding one VE by hand (requested 2026-08-10) — the prospect a team is watching before they have
/// ever worked a session.
///
/// <para>Every case here is about <b>not creating a rival record</b>. This is the third way a person
/// can enter the table (after the ExamTools sync and the CSV import), and the other two already had
/// to earn their matching rules the hard way.</para>
/// </summary>
public class VeManualAddTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerImportService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, Team team, string callSign, string name)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        dbContext.VeTeamMemberships.Add(new VeTeamMembership
        {
            VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true, CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
        return person;
    }

    [Fact]
    public async Task AddingSomeoneNewCreatesThePersonAndTheirMembership()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");

        var result = await CreateService(dbContext)
            .AddOneAsync(team.Id, "KF0JZP", "Alaric Hanson", "alaric@example.com", null, 1, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(VeImportAction.Create, result.Action);
        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal("KF0JZP", person.CallSign);
        Assert.Equal("alaric@example.com", person.Email);
        Assert.Equal(team.Id, (await dbContext.VeTeamMemberships.SingleAsync()).TeamId);
    }

    /// <summary>
    /// The person model's whole point. Someone serving MARC who a HRCC admin adds must gain a second
    /// membership, not a second identity — otherwise their session history splits in two.
    /// </summary>
    [Fact]
    public async Task AddingSomeoneWhoAlreadyServesAnotherTeamGivesThemAMembership()
    {
        await using var dbContext = CreateContext();
        var marc = await SeedTeamAsync(dbContext, "MARC");
        var hrcc = await SeedTeamAsync(dbContext, "HRCC");
        await SeedVeAsync(dbContext, marc, "N2SPG", "Sam Granger");

        var result = await CreateService(dbContext)
            .AddOneAsync(hrcc.Id, "n2spg", null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeImportAction.AddToTeam, result.Action);
        Assert.Single(dbContext.VolunteerExaminers);                       // still one person
        Assert.Equal(2, await dbContext.VeTeamMemberships.CountAsync());   // now on both teams
        Assert.Equal("Sam Granger", (await dbContext.VolunteerExaminers.SingleAsync()).Name);
    }

    [Fact]
    public async Task AddingSomeoneAlreadyOnThisTeamChangesNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var result = await CreateService(dbContext)
            .AddOneAsync(team.Id, "N2SPG", null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeImportAction.Update, result.Action);
        Assert.Single(dbContext.VolunteerExaminers);
        Assert.Single(dbContext.VeTeamMemberships);
    }

    /// <summary>A hand-typed name must not overwrite the one already on file — same "blank means no opinion" rule the importer follows, and the sync stopped re-applying ExamTools' name for the same reason.</summary>
    [Fact]
    public async Task AddingAnExistingPersonFillsBlanksButDoesNotOverwrite()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        person.Email = "sam@example.com";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext)
            .AddOneAsync(team.Id, "N2SPG", null, "different@example.com", "555-0100", 1, CancellationToken.None);

        var reloaded = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal("sam@example.com", reloaded.Email);  // not overwritten
        Assert.Equal("555-0100", reloaded.Phone);         // was blank, so filled
    }

    /// <summary>ExamTools' "&lt;UNKNOWN&gt;" fused two real people once already. A hand-typed placeholder must not open that door again.</summary>
    [Theory]
    [InlineData("<UNKNOWN>")]
    [InlineData("unknown")]
    [InlineData("12345")]
    public async Task APlaceholderCallSignIsRejected(string callSign)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");

        var result = await CreateService(dbContext)
            .AddOneAsync(team.Id, callSign, "Someone", null, null, 1, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(dbContext.VolunteerExaminers);
    }

    [Fact]
    public async Task AddingNothingAtAllIsRejected()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");

        var result = await CreateService(dbContext)
            .AddOneAsync(team.Id, "  ", "  ", null, null, 1, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(dbContext.VolunteerExaminers);
    }

    /// <summary>
    /// The reconciliation that makes this feature safe to use at all: a prospect added by hand who
    /// later works a real session must be <b>matched</b> by the ExamTools sync, not duplicated.
    ///
    /// <para>Asserted here at the level that actually decides it — the sync matches on a usable call
    /// sign across the whole table, and this record has one. If that ever changes, adding prospects
    /// silently becomes a duplicate factory, and the duplicate only appears weeks later when they
    /// finally work a session.</para>
    /// </summary>
    [Fact]
    public async Task AHandAddedProspectIsMatchedByCallSignNotDuplicated()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        await CreateService(dbContext).AddOneAsync(team.Id, "KF0JZP", "Alaric Hanson", null, null, 1, CancellationToken.None);

        // What the sync does when ExamTools first reports them on a roster: look for a usable call
        // sign already in the table.
        var lookup = await dbContext.VolunteerExaminers
            .Where(v => CallSign.IsUsable(v.CallSign))
            .ToListAsync();

        var matched = lookup.SingleOrDefault(v => string.Equals(v.CallSign, "KF0JZP", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(matched);
        Assert.Equal("Alaric Hanson", matched!.Name);
    }
}
