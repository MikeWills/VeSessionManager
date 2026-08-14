using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Bulk VE import (issue #142 phase 4). The cases that matter are the ones where an import could
/// quietly damage existing data: blanking a field, creating a second record for someone already
/// known, or accepting an unidentifiable row.
/// </summary>
public class VolunteerExaminerImportServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerImportService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, string callSign, string name, Team? team = null, string? frn = null, string? phone = null)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, Frn = frn, Phone = phone, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        if (team is not null)
        {
            dbContext.VeTeamMemberships.Add(new VeTeamMembership
            {
                VolunteerExaminer = person, Team = team, IsActive = true, CreatedUtc = Now
            });
        }

        await dbContext.SaveChangesAsync();
        return person;
    }

    private const string Header = "CallSign,Name,Email,Phone,City";

    [Fact]
    public async Task NewRows_AreCreatedAndJoinTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var csv = $"{Header}\nN2SPG,Sam Granger,sam@example.com,555-0001,Mankato";

        var result = await CreateService(dbContext).ApplyAsync(csv, team.Id, userId: 1, CancellationToken.None);

        Assert.Equal(1, result.Created);
        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal("N2SPG", person.CallSign);
        Assert.Equal("sam@example.com", person.Email);
        Assert.Single(dbContext.VeTeamMemberships);
    }

    /// <summary>
    /// Import is the other duplicate-generating path, so it uses the same identity rules as the sync:
    /// someone already serving another team gains a membership, not a second record.
    /// </summary>
    [Fact]
    public async Task KnownPersonFromAnotherTeam_GainsAMembershipNotASecondRecord()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "HRCC");
        var teamB = await SeedTeamAsync(dbContext, "MARC");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", teamA);

        var preview = await CreateService(dbContext).ParseAsync($"{Header}\nN2SPG,Sam Granger,,,", teamB.Id, visibleTeamIds: null, CancellationToken.None);
        Assert.Equal(VeImportAction.AddToTeam, Assert.Single(preview.Rows).Action);

        var result = await CreateService(dbContext).ApplyAsync($"{Header}\nN2SPG,Sam Granger,,,", teamB.Id, 1, CancellationToken.None);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.AddedToTeam);
        Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal(2, dbContext.VeTeamMemberships.Count());
    }

    /// <summary>
    /// A blank cell means "no opinion", never "delete". A spreadsheet that omits a column must not
    /// silently empty a phone number nobody notices is gone until they need it.
    /// </summary>
    [Fact]
    public async Task BlankCells_DoNotEraseExistingValues()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", team, phone: "555-0001");

        await CreateService(dbContext).ApplyAsync($"{Header}\nN2SPG,Sam Granger,,,Mankato", team.Id, 1, CancellationToken.None);

        var person = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal("555-0001", person.Phone);   // untouched
        Assert.Equal("Mankato", person.City);     // filled
    }

    /// <summary>FRN wins over call sign, so a file listing someone by a call sign they no longer hold still finds them.</summary>
    [Fact]
    public async Task FrnMatches_EvenWhenTheCallSignHasChanged()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVeAsync(dbContext, "W1XYZ", "Sam Granger", team, frn: "0004511143");

        var csv = "CallSign,Name,Frn\nKF0JZP,Sam Granger,0004511143";
        var preview = await CreateService(dbContext).ParseAsync(csv, team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.Equal(VeImportAction.Update, Assert.Single(preview.Rows).Action);

        await CreateService(dbContext).ApplyAsync(csv, team.Id, 1, CancellationToken.None);
        Assert.Single(dbContext.VolunteerExaminers);
    }

    /// <summary>An FRN typed into a spreadsheet is not accepted — it is the identity key, it is unique, and the ULS sweep fills it from FCC.</summary>
    [Fact]
    public async Task FrnFromTheFile_IsNotWrittenToANewRecord()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await CreateService(dbContext).ApplyAsync("CallSign,Name,Frn\nN2SPG,Sam Granger,0004511143", team.Id, 1, CancellationToken.None);

        Assert.Null((await dbContext.VolunteerExaminers.SingleAsync()).Frn);
    }

    [Fact]
    public async Task UnusableCallSign_IsRejectedNotImported()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync($"{Header}\n<UNKNOWN>,Someone,,,", team.Id, visibleTeamIds: null, CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.False(row.IsValid);
        Assert.Contains("not a usable call sign", row.Problem);
    }

    [Fact]
    public async Task SameCallSignTwiceInOneFile_IsAnErrorOnTheSecond()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync(
            $"{Header}\nN2SPG,Sam Granger,,,\nN2SPG,Samuel Granger,,,", team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.Rows[0].IsValid);
        Assert.False(preview.Rows[1].IsValid);
        Assert.Contains("more than once", preview.Rows[1].Problem);
    }

    [Fact]
    public async Task RowWithNeitherNameNorCallSign_IsRejected()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync($"{Header}\n,,someone@example.com,,", team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.False(Assert.Single(preview.Rows).IsValid);
    }

    [Fact]
    public async Task MissingHeader_IsReportedRatherThanImportingGarbage()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync("N2SPG,Sam Granger\nNP2UU,Uma Unwin", team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Rows);
    }

    /// <summary>Quoted fields with embedded commas and doubled quotes — the format this app's own export writes.</summary>
    [Fact]
    public async Task QuotedFields_AreParsed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign,Name,City\n\"N2SPG\",\"Granger, Sam \"\"Sammy\"\"\",\"Mankato\"", team.Id, visibleTeamIds: null, CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Granger, Sam \"Sammy\"", row.Name);
        Assert.Equal("Mankato", row.City);
    }

    /// <summary>
    /// The export prefixes an apostrophe to anything Excel would evaluate as a formula. A round trip
    /// must strip it again, or a name gains one more apostrophe on every export/import cycle.
    /// </summary>
    [Fact]
    public async Task ExportedFormulaGuard_IsStrippedOnTheWayBackIn()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign,Name\nN2SPG,\"'=Granger\"", team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.Equal("=Granger", Assert.Single(preview.Rows).Name);
    }

    [Fact]
    public async Task PreviewCounts_MatchWhatApplyDoes()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", team);

        var csv = $"{Header}\nN2SPG,Sam Granger,,,\nNP2UU,Uma Unwin,,,\n<UNKNOWN>,Nobody,,,";
        var preview = await CreateService(dbContext).ParseAsync(csv, team.Id, visibleTeamIds: null, CancellationToken.None);

        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);
        Assert.Equal(1, preview.InvalidCount);

        var result = await CreateService(dbContext).ApplyAsync(csv, team.Id, 1, CancellationToken.None);
        Assert.Equal(preview.CreateCount, result.Created);
        Assert.Equal(preview.UpdateCount, result.Updated);
        Assert.Equal(preview.InvalidCount, result.Skipped);
    }

    // ---- #240: the preview must not be an existence-and-name oracle over other teams ----

    /// <summary>
    /// The finding itself. Upload call signs with no Name column, stop at the preview, and every
    /// AddToTeam row was "this person exists on a team that is not yours" — with their real name
    /// rendered beside it, 500 probes per request, and nothing audited (VeDirectoryImported is only
    /// written on apply).
    /// </summary>
    [Fact]
    public async Task Preview_DoesNotRevealTheNameOfAVeOnAnotherTeam()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        var theirs = await SeedTeamAsync(dbContext, "THEIRS");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", theirs);

        // No Name column at all — the whole point of the probe.
        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign\nN2SPG", mine.Id, visibleTeamIds: [mine.Id], CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.DoesNotContain("Sam Granger", row.DisplayName);
        Assert.Equal("N2SPG", row.DisplayName);
        Assert.Equal(VeImportAction.Create, row.DisplayAction);
    }

    /// <summary>
    /// The other half, and the reason this is a display fix rather than a query fix: the row must
    /// still MATCH, or apply would create a second record for someone already known — a worse bug
    /// than the disclosure. Redacting the match instead of the display is the wrong fix.
    /// </summary>
    [Fact]
    public async Task Preview_StillMatchesTheHiddenVe_SoApplyDoesNotDuplicateThem()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        var theirs = await SeedTeamAsync(dbContext, "THEIRS");
        var person = await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", theirs);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign\nN2SPG", mine.Id, visibleTeamIds: [mine.Id], CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(person.Id, row.MatchedVolunteerExaminerId);
        Assert.Equal(VeImportAction.AddToTeam, row.Action);

        var result = await CreateService(dbContext).ApplyAsync("CallSign\nN2SPG", mine.Id, 1, CancellationToken.None);

        Assert.Equal(1, result.AddedToTeam);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, await dbContext.VolunteerExaminers.CountAsync(v => v.CallSign == "N2SPG"));
    }

    /// <summary>
    /// Apply must not overwrite a real name with a call sign. Name falls back to the matched record
    /// precisely so this cannot happen, which is why the redaction lives on DisplayName instead —
    /// blanking Name would have traded a disclosure for data loss.
    /// </summary>
    [Fact]
    public async Task Apply_DoesNotOverwriteAnExistingNameWhenTheFileOmitsOne()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        var theirs = await SeedTeamAsync(dbContext, "THEIRS");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", theirs);

        await CreateService(dbContext).ApplyAsync("CallSign\nN2SPG", mine.Id, 1, CancellationToken.None);

        var person = await dbContext.VolunteerExaminers.SingleAsync(v => v.CallSign == "N2SPG");
        Assert.Equal("Sam Granger", person.Name);
    }

    /// <summary>A VE on the importer's own team is theirs to see — redacting that would make the
    /// preview useless for the case it exists to serve.</summary>
    [Fact]
    public async Task Preview_ShowsTheNameOfAVeOnTheImportersOwnTeam()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", mine);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign\nN2SPG", mine.Id, visibleTeamIds: [mine.Id], CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Sam Granger", row.DisplayName);
        Assert.Equal(VeImportAction.Update, row.DisplayAction);
    }

    /// <summary>
    /// Null means every team, not no teams — the SystemAdmin case. Written as
    /// <c>visibleTeamIds?.Contains(...) ?? false</c> this inverts and hides everything from the one
    /// role that can see everything (the trap CLAUDE.md records).
    /// </summary>
    [Fact]
    public async Task Preview_ShowsEverythingWhenVisibleTeamIdsIsNull()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        var theirs = await SeedTeamAsync(dbContext, "THEIRS");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", theirs);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign\nN2SPG", mine.Id, visibleTeamIds: null, CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Sam Granger", row.DisplayName);
        Assert.Equal(VeImportAction.AddToTeam, row.DisplayAction);
    }

    /// <summary>The counts are the same oracle aggregated — "1 will be added to the team" answers
    /// the existence question without naming anyone.</summary>
    [Fact]
    public async Task Preview_CountsFollowTheRedactedActions()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext, "MINE");
        var theirs = await SeedTeamAsync(dbContext, "THEIRS");
        await SeedVeAsync(dbContext, "N2SPG", "Sam Granger", theirs);

        var preview = await CreateService(dbContext).ParseAsync(
            "CallSign\nN2SPG", mine.Id, visibleTeamIds: [mine.Id], CancellationToken.None);

        Assert.Equal(0, preview.AddToTeamCount);
        Assert.Equal(1, preview.CreateCount);
    }
}
