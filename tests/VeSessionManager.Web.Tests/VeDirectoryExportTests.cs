using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The VE Directory's CSV export ships <b>everything matching the filters</b>, not the page the
/// admin happened to be looking at.
///
/// <para><b>Why this test exists.</b> The export handler used to call <c>OnGetAsync</c> and iterate
/// whatever that left in <c>Rows</c>. When the page gained paging (#298), that would silently have
/// become "export page 1" — and verified by mutation, the entire 237-test Web suite passed with
/// exactly that bug in place. A short CSV is not obviously wrong to look at, and the audit row that
/// exists specifically to attest a copy of contact details left the building would have counted the
/// page rather than the export.</para>
/// </summary>
public class VeDirectoryExportTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public VeDirectoryExportTests(WebAppFactory factory) => _factory = factory;

    /// <summary>Comfortably more than one page, so a paged export is visibly short.</summary>
    private const int SeededVeCount = 60;

    private async Task SeedVesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Not wiping the existing VEs: the factory's seeded session has a VE roster pointing at
        // them, and real SQLite enforces that. These rows are isolated by the search filter instead,
        // which also makes the expected totals exact rather than "however many were already there".
        // Idempotent: the fixture is shared across this class's tests, so seeding twice would double
        // the roster and make the expected totals wrong in a way that reads like a paging bug.
        if (await db.VolunteerExaminers.AnyAsync(v => v.Name == "Export VE 000"))
        {
            return;
        }

        var teamId = (await db.Teams.AsNoTracking().FirstAsync()).Id;

        for (var i = 0; i < SeededVeCount; i++)
        {
            var person = new VolunteerExaminer
            {
                Name = $"Export VE {i:D3}",
                CallSign = $"K0X{i:D3}",
                CreatedUtc = DateTime.UtcNow
            };
            db.VolunteerExaminers.Add(person);
            await db.SaveChangesAsync();

            db.VeTeamMemberships.Add(new VeTeamMembership
            {
                VolunteerExaminerId = person.Id,
                TeamId = teamId,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Posts the way a browser does — Razor Pages rejects a POST with no token in middleware.</summary>
    private static async Task<HttpResponseMessage> PostExportAsync(HttpClient client)
    {
        var page = await client.GetStringAsync("/SessionManager/VeDirectory");
        var token = Regex
            .Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);

        return await client.PostAsync(
            "/SessionManager/VeDirectory?handler=Export&search=Export%20VE",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token)]));
    }

    [Fact]
    public async Task ExportContainsEveryMatchingVe_NotJustTheFirstPage()
    {
        await SeedVesAsync();
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostExportAsync(client);
        Assert.True(response.IsSuccessStatusCode, $"Export returned {(int)response.StatusCode}.");

        var csv = await response.Content.ReadAsStringAsync();
        var exported = Regex.Matches(csv, @"Export VE \d{3}").Count;

        Assert.Equal(SeededVeCount, exported);
    }

    /// <summary>
    /// The page itself is paged, which is the other half of the same change — asserted here so this
    /// file cannot pass by the export and the page both quietly loading everything.
    /// </summary>
    [Fact]
    public async Task ThePageItselfShowsOnePageAndSaysHowManyThereAre()
    {
        await SeedVesAsync();
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/VeDirectory?search=Export%20VE&pageSize=25");

        Assert.Equal(25, Regex.Matches(html, @"Export VE \d{3}").Count);
        Assert.Contains($"of {SeededVeCount}", html);
    }
}
