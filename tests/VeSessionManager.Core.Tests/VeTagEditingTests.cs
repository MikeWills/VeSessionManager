using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Editing a tag in place, and the colour that came with it (requested 2026-08-09).
///
/// <para>The reason editing exists at all is the reason the first test here matters: before it, the
/// only way to change a tag's order or name was <b>delete and re-add</b>, and deleting cascades the
/// assignments away. Correcting a display detail silently untagged every VE who had that tag.</para>
/// </summary>
public class VeTagEditingTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static VolunteerExaminerManagementService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    // ---- editing keeps assignments ----

    /// <summary>
    /// The whole point of editing in place. A VE tagged "Team member" must still be tagged after the
    /// tag is renamed and reordered — which is true only because the row keeps its id.
    /// </summary>
    [Fact]
    public async Task UpdatingATagKeepsEveryAssignment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(team.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        var person = new VolunteerExaminer { Name = "Alaric Hanson", CallSign = "KF0JZP" };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        var membership = new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true };
        dbContext.VeTeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync();
        dbContext.Set<VeTagAssignment>().Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag!.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await service.UpdateTagAsync(tag.Id, "Full member", 5, "#3366CC", null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        var stored = await dbContext.VeTags.SingleAsync();
        Assert.Equal("Full member", stored.Name);
        Assert.Equal(5, stored.SortOrder);
        Assert.Equal("#3366cc", stored.Color); // normalised to lower case
        Assert.Equal(1, await dbContext.Set<VeTagAssignment>().CountAsync());
    }

    [Fact]
    public async Task RenamingOntoAnotherTagsNameIsRejected()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateTagAsync(team.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);
        var (_, second) = await service.CreateTagAsync(team.Id, "Team lead", 1, null, null, null, 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(second!.Id, "Team member", 1, null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.DuplicateTagName, result);
        Assert.Equal("Team lead", (await dbContext.VeTags.FindAsync(second.Id))!.Name);
    }

    /// <summary>
    /// The duplicate check must exclude the row being edited, or saving a tag without touching its
    /// name would report a clash with itself.
    /// </summary>
    [Fact]
    public async Task SavingATagWithItsOwnNameUnchangedIsAllowed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(team.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(tag!.Id, "Team member", 3, null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Equal(3, (await dbContext.VeTags.FindAsync(tag.Id))!.SortOrder);
    }

    /// <summary>Two teams may each have a "Team member" — the uniqueness is per team, and editing must not reach across.</summary>
    [Fact]
    public async Task AnotherTeamsIdenticalTagNameIsNotAClash()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "HRCC");
        var teamB = await SeedTeamAsync(dbContext, "MARC");
        var service = CreateService(dbContext);
        await service.CreateTagAsync(teamA.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);
        var (_, other) = await service.CreateTagAsync(teamB.Id, "Provisional", 0, null, null, null, 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(other!.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
    }

    // ---- colour validation ----

    /// <summary>
    /// A tag colour is written into a CSS custom property, and HTML-encoding does not make a string
    /// safe in a stylesheet. Anything that isn't exactly #RRGGBB is refused rather than stored and
    /// filtered later.
    /// </summary>
    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("rgb(255,0,0)")]
    [InlineData("#ff0000; background-image: url(https://evil.example/x)")]
    [InlineData("#12345g")]
    public async Task AnInvalidColorIsRejectedRatherThanStored(string color)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);

        var (createResult, _) = await service.CreateTagAsync(team.Id, "Team member", 0, color, null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.InvalidColor, createResult);
        Assert.Empty(dbContext.VeTags);
    }

    [Fact]
    public async Task ClearingTheColorStoresNull()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(team.Id, "Team member", 0, "#3366cc", null, null, 1, CancellationToken.None);

        await service.UpdateTagAsync(tag!.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        Assert.Null((await dbContext.VeTags.FindAsync(tag.Id))!.Color);
    }

    /// <summary>The render-side half of the guard: a value that reached the database some other way still cannot be emitted.</summary>
    [Fact]
    public void ForStyleRefusesAnythingThatIsNotSixDigitHex()
    {
        Assert.Equal("#3366cc", VeTagColor.ForStyle("#3366cc"));
        Assert.Null(VeTagColor.ForStyle("#fff"));
        Assert.Null(VeTagColor.ForStyle("red; background-image: url(https://evil.example/x)"));
        Assert.Null(VeTagColor.ForStyle(null));
    }

    // ---- "highest tag colour wins" ----

    /// <summary>
    /// "Highest" is the tag shown first, which is the LOWEST SortOrder. Worth a test of its own
    /// because the two phrasings read like opposites.
    /// </summary>
    [Fact]
    public void TheHighestPriorityColoredTagWins()
    {
        var tags = new[]
        {
            new VeTag { Name = "Team member", SortOrder = 5, Color = "#aaaaaa" },
            new VeTag { Name = "Team lead", SortOrder = 1, Color = "#3366cc" }
        };

        Assert.Equal("#3366cc", VeTagColor.ForTags(tags));
    }

    /// <summary>
    /// An uncoloured top tag doesn't win by being first — it has no colour to contribute. Someone who
    /// colours only their "Team lead" tag expects that colour to show, not to be beaten by the
    /// colourless tag above it.
    /// </summary>
    [Fact]
    public void AnUncoloredHigherTagDoesNotSuppressALowerColoredOne()
    {
        var tags = new[]
        {
            new VeTag { Name = "Team member", SortOrder = 0, Color = null },
            new VeTag { Name = "Team lead", SortOrder = 2, Color = "#3366cc" }
        };

        Assert.Equal("#3366cc", VeTagColor.ForTags(tags));
    }

    [Fact]
    public void NoColoredTagsMeansNoColor()
    {
        var tags = new[] { new VeTag { Name = "Team member", SortOrder = 0, Color = null } };

        Assert.Null(VeTagColor.ForTags(tags));
        Assert.Null(VeTagColor.ForTags([]));
    }

    /// <summary>An invalid stored colour is skipped by the winner rule too, rather than winning and then rendering as nothing.</summary>
    [Fact]
    public void AnInvalidStoredColorIsSkippedInFavourOfAValidLowerOne()
    {
        var tags = new[]
        {
            new VeTag { Name = "Team member", SortOrder = 0, Color = "not-a-color" },
            new VeTag { Name = "Team lead", SortOrder = 1, Color = "#3366cc" }
        };

        Assert.Equal("#3366cc", VeTagColor.ForTags(tags));
    }
}
