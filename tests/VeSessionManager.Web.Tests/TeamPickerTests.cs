using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The shared team picker renders what the twelve pages using it used to render themselves (#306).
///
/// <para><b>Nothing covered this before.</b> The fixture seeds one team, and most pages only show a
/// picker when there is more than one — so every existing test rendered these pages with the picker
/// absent. A partial that emitted nothing at all would have passed the whole suite. This seeds a
/// second team so the component is actually on the page.</para>
/// </summary>
public class TeamPickerTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public TeamPickerTests(WebAppFactory factory) => _factory = factory;

    private const string SecondTeamName = "SECOND-TEAM";

    /// <summary>
    /// Ensures more than one team exists, and reports how many there are. The count is read back
    /// rather than assumed: other suites seed teams into this fixture too, and a hardcoded
    /// expectation would fail for a reason that has nothing to do with the picker.
    /// </summary>
    private async Task<int> EnsureSeveralTeamsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Teams.AnyAsync(t => t.Name == SecondTeamName))
        {
            db.Teams.Add(new Team { Name = SecondTeamName, ExamToolsTeamCode = "SECOND" });
            await db.SaveChangesAsync();
        }

        return await db.Teams.CountAsync();
    }

    private async Task<(string Html, int TeamCount)> GetAsync(string url)
    {
        var teamCount = await EnsureSeveralTeamsAsync();
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        return (await client.GetStringAsync(url), teamCount);
    }

    /// <summary>Counts the picker's own radios, which are the only teamId radios on any of these pages.</summary>
    private static int TeamRadioCount(string html) =>
        Regex.Matches(html, @"<input type=""radio"" name=""teamId""").Count;

    /// <summary>
    /// The common shape: an "All teams" option plus one radio per team. A merged view is meaningful
    /// on a list, so the option must be there.
    /// </summary>
    [Fact]
    public async Task AListPageOffersAllTeamsPlusEachTeam()
    {
        var (html, teamCount) = await GetAsync("/SessionManager/Index?applied=true");

        Assert.True(teamCount > 1, "The picker only renders with more than one team.");
        Assert.Contains("All teams", html);
        Assert.Contains(SecondTeamName, html);

        // One radio per team, plus the All-teams option.
        Assert.Equal(teamCount + 1, TeamRadioCount(html));
    }

    /// <summary>
    /// The admin shape: no "All teams". These pages edit one team's configuration, so a merged view
    /// has no meaning — and without the option there is no way to express "no filter", which makes
    /// this a behavioral difference rather than a cosmetic one.
    /// </summary>
    [Fact]
    public async Task AnAdminConfigPageOffersNoAllTeamsOption()
    {
        var (html, teamCount) = await GetAsync("/Admin/MessageRules");

        Assert.Contains(SecondTeamName, html);
        Assert.Equal(teamCount, TeamRadioCount(html));

        // The radio itself must be absent, not merely the words — "All teams" also appears in prose
        // elsewhere on some pages.
        Assert.DoesNotMatch(
            new Regex(@"<input type=""radio"" name=""teamId"" value="""" "), html);
    }

    /// <summary>
    /// The worklist shape: a per-team badge answering "how much is outstanding here" before you pick.
    /// Rendered through TeamCountExtensions.CountFor, since a team with nothing outstanding is absent
    /// from the dictionary rather than present as zero.
    /// </summary>
    [Fact]
    public async Task AWorklistPageRendersPerTeamCounts()
    {
        var (html, teamCount) = await GetAsync("/SessionManager/ApplicantStatus");

        Assert.Equal(teamCount + 1, TeamRadioCount(html));
        Assert.Contains("pill-count", html);
    }

    /// <summary>
    /// VE Directory keeps its own copy, because of the extra "Manage this team's tags" link inside
    /// the menu. Asserted so the exception stays deliberate: if someone folds it into the partial,
    /// this fails and they have to decide about the link rather than lose it.
    /// </summary>
    [Fact]
    public async Task VeDirectoryStillRendersItsOwnPickerWithTheTagsLink()
    {
        // With a team chosen: the extra link is deliberately absent on "all teams", where there is
        // no single tag vocabulary to edit.
        await EnsureSeveralTeamsAsync();
        int teamId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            teamId = (await db.Teams.AsNoTracking().FirstAsync(t => t.Name == SecondTeamName)).Id;
        }

        var (html, teamCount) = await GetAsync($"/SessionManager/VeDirectory?teamId={teamId}");

        Assert.Equal(teamCount + 1, TeamRadioCount(html));
        Assert.Contains("Manage this team", html);
        Assert.Contains("/SessionManager/VeTags", html);
    }
}
