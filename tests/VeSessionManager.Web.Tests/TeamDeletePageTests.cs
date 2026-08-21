using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The Teams page's permanent delete.
///
/// <para>What the service does with the data is covered against real SQLite in
/// <c>TeamDeletionSqliteTests</c>. What matters here is everything wrapped around it: who may press
/// it, and the typed-name guard — <b>which is checked on the server, because a modal is not a
/// permission</b>. A hand-built POST never opens the dialog at all.</para>
/// </summary>
public class TeamDeletePageTests
{
    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static async Task<HttpResponseMessage> PostDeleteAsync(HttpClient client, int teamId, string confirmName) =>
        await client.PostAsync("/Admin/Teams?handler=Delete", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("teamId", teamId.ToString()),
            new KeyValuePair<string, string>("confirmName", confirmName),
            new KeyValuePair<string, string>("__RequestVerificationToken", await AntiforgeryTokenAsync(client, "/Admin/Teams"))
        ]));

    /// <summary>
    /// ⚠️ The role HEADER only shapes the claims principal; <c>CanCreateTeam</c> reads
    /// <c>User.Role</c> off the database row, which the harness seeds as SystemAdmin. Demoting the
    /// row is the only way to exercise the real guard — the header alone would leave the acting user
    /// a SystemAdmin and quietly prove nothing.
    /// </summary>
    private static async Task DemoteActingUserAsync(WebAppFactory factory, UserRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync();
        user.Role = role;
        await db.SaveChangesAsync();
    }

    private static async Task<(string Name, bool Exists)> ReadTeamAsync(WebAppFactory factory, int teamId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId);
        return (team?.Name ?? "", team is not null);
    }

    [Fact]
    public async Task TypingTheNameExactly_DeletesTheTeam()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var teamId = factory.Seeded.TeamId;
        var (name, _) = await ReadTeamAsync(factory, teamId);

        var response = await PostDeleteAsync(client, teamId, name);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False((await ReadTeamAsync(factory, teamId)).Exists);
    }

    /// <summary>
    /// ⚠️ The guard that earns its place. The mistake this action invites is pressing delete on the
    /// right-looking row of the <i>wrong</i> team, and a near-miss is exactly what that produces.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not the team")]
    [InlineData("wx0mik ")]
    public async Task AnythingButTheExactName_DeletesNothing(string typed)
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var teamId = factory.Seeded.TeamId;

        var response = await PostDeleteAsync(client, teamId, typed);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await ReadTeamAsync(factory, teamId)).Exists);
    }

    /// <summary>
    /// Case matters, and deliberately: the comparison is <c>Ordinal</c>. Somebody who types the name
    /// from memory rather than reading the row in front of them is the person this guard is for.
    /// </summary>
    [Fact]
    public async Task TheWrongCase_DeletesNothing()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var teamId = factory.Seeded.TeamId;
        var (name, _) = await ReadTeamAsync(factory, teamId);

        var response = await PostDeleteAsync(client, teamId, name.ToLowerInvariant() + "x");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await ReadTeamAsync(factory, teamId)).Exists);
    }

    /// <summary>
    /// A TeamAdmin runs their team; they do not get to remove it. Same gate as creating one, because
    /// both change the shape of the deployment rather than configuring a team.
    /// </summary>
    [Fact]
    public async Task ATeamAdmin_CannotDeleteATeam()
    {
        using var factory = new WebAppFactory();
        await DemoteActingUserAsync(factory, UserRole.TeamAdmin);
        var client = factory.CreateClientAs(UserRole.TeamAdmin);
        var teamId = factory.Seeded.TeamId;
        var (name, _) = await ReadTeamAsync(factory, teamId);

        var response = await PostDeleteAsync(client, teamId, name);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((await ReadTeamAsync(factory, teamId)).Exists);
    }

    /// <summary>And the menu entry is not offered to them either — the handler is the real guard, but a button that always refuses is its own kind of bug.</summary>
    [Fact]
    public async Task ATeamAdmin_IsNotOfferedTheDeleteEntry()
    {
        using var factory = new WebAppFactory();
        var asSystemAdmin = await factory.CreateClientAs(UserRole.SystemAdmin).GetStringAsync("/Admin/Teams");
        Assert.Contains("Delete permanently", asSystemAdmin);

        await DemoteActingUserAsync(factory, UserRole.TeamAdmin);
        var asTeamAdmin = await factory.CreateClientAs(UserRole.TeamAdmin).GetStringAsync("/Admin/Teams");
        Assert.DoesNotContain("Delete permanently", asTeamAdmin);
    }

    /// <summary>The confirmation says what goes, in numbers somebody can check against what they expect.</summary>
    [Fact]
    public async Task TheConfirmation_SaysWhatWillBeDeleted()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/Admin/Teams");

        Assert.Contains("cannot be undone", html);
        Assert.Contains("session(s)", html);
        Assert.Contains("candidate(s)", html);
        // The reversible alternative is named, since it is what most people actually want.
        Assert.Contains("<strong>Deactivate</strong> instead", html);
    }
}
