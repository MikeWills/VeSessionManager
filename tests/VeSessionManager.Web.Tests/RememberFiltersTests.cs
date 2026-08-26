using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Filters survive navigating away and back (#459).
///
/// <para>Mike: <i>"Ensure that all table filters and sorting is saved so it sticks when navigating
/// back to that page. I know some do not currently save."</i></para>
///
/// <para><b>Sorting was already fine</b> — <c>app.js</c> keeps each sortable table's column and
/// direction in localStorage, per page and table. These tests are about the filters, which had one
/// implementation (the sessions list's cookie) and nothing anywhere else.</para>
///
/// <para>⚠️ Half of this file is about what the mechanism could break rather than what it should do:
/// a redirect loop, a remembered filter following somebody onto a page they did not ask for, and a
/// cleared filter springing back. Those are the ways this goes wrong quietly.</para>
/// </summary>
public class RememberFiltersTests
{
    private const string Directory = "/SessionManager/VeDirectory";

    /// <summary>
    /// CreateClientAs already disables auto-redirect and keeps cookies across requests, which is what
    /// makes these tests possible at all: the redirect itself can be asserted on rather than its
    /// destination, and the remembered filters ride along the way a browser's would.
    /// </summary>
    private static HttpClient RawClient(WebAppFactory factory, UserRole role = UserRole.SystemAdmin) =>
        factory.CreateClientAs(role);

    [Fact]
    public async Task AFilteredVisit_IsRememberedAndRestoredOnABareOne()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);

        var filtered = await client.GetAsync($"{Directory}?search=glory");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);

        var bare = await client.GetAsync(Directory);

        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Contains("search=glory", bare.Headers.Location!.ToString());
    }

    /// <summary>
    /// ⚠️ The loop. The redirect always carries a query string, so the request it produces takes the
    /// "filters present" branch and stops — but a mistake here would send a browser round forever, so
    /// it is asserted rather than reasoned about.
    /// </summary>
    [Fact]
    public async Task TheRestoredRequest_DoesNotRedirectAgain()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);

        await client.GetAsync($"{Directory}?search=glory");
        var first = await client.GetAsync(Directory);
        var second = await client.GetAsync(first.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    /// <summary>Nothing remembered means the page renders its own defaults, exactly as before.</summary>
    [Fact]
    public async Task WithNothingRemembered_ABareVisitJustRenders()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);

        var response = await client.GetAsync(Directory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// ⚠️ Clearing a filter has to stick. An emptied text box still submits its key, so the request
    /// carries a query string and counts as deliberate — this pins that, because if it ever stopped
    /// being true the old filter would spring back and look like the page ignoring you.
    /// </summary>
    [Fact]
    public async Task ClearingAFilter_IsRememberedAsCleared()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);

        await client.GetAsync($"{Directory}?search=glory");
        var cleared = await client.GetAsync($"{Directory}?search=");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var bare = await client.GetAsync(Directory);

        if (bare.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.DoesNotContain("glory", bare.Headers.Location!.ToString());
        }
    }

    /// <summary>
    /// ⚠️ A named handler is an action, not a view of the list. Redirecting one out from under itself
    /// would drop the POST-back or run it against the wrong filters.
    /// </summary>
    [Fact]
    public async Task ANamedHandlerIsNeverRedirected()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        await client.GetAsync($"{Directory}?search=glory");

        var response = await client.GetAsync($"{Directory}?handler=Export");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>
    /// One page's filters must not leak onto another. The cookie is keyed by path, and this is the
    /// assertion that keeps it that way.
    /// </summary>
    [Fact]
    public async Task FiltersAreRememberedPerPage()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);

        await client.GetAsync($"{Directory}?search=glory");
        var otherPage = await client.GetAsync("/SessionManager/UnmatchedPayments");

        Assert.Equal(HttpStatusCode.OK, otherPage.StatusCode);
    }

    /// <summary>
    /// ⚠️ A page whose query string carries a <i>target</i> rather than a filter must never opt in —
    /// a remembered id would redirect somebody onto a record they did not ask for. This is a source
    /// scan rather than a rule anyone has to remember.
    /// </summary>
    [Fact]
    public void NoTargetTakingPageOptsIn()
    {
        var assembly = typeof(RememberFiltersPageFilter).Assembly;
        var offenders = assembly.GetTypes()
            .Where(t => typeof(PageModel).IsAssignableFrom(t))
            .Where(t => t.IsDefined(typeof(RemembersFiltersAttribute), inherit: true))
            .Where(t => t.Name is "CandidateEmailModel" or "VeEmailModel" or "VeInviteModel"
                     or "DetailModel" or "CandidateDetailModel" or "MessageRuleEditModel")
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These take a target in the query string, so remembering it would send somebody to a record "
            + "they did not ask for:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The sessions list keeps its own cookie, with an <c>Applied</c> field deciding apply-vs-restore.
    /// Opting it in as well would give one page two answers to the same question.
    /// </summary>
    [Fact]
    public void TheSessionsListDoesNotOptIn()
    {
        var sessions = typeof(RememberFiltersPageFilter).Assembly
            .GetTypes()
            .Single(t => t.FullName == "VeSessionManager.Web.Pages.SessionManager.IndexModel");

        Assert.False(sessions.IsDefined(typeof(RemembersFiltersAttribute), inherit: true));
    }

    /// <summary>
    /// Cross-page team persistence (2026-08-26): a team picked on one team-filtered page carries to
    /// another, unlike every other filter (which stays scoped per page — see
    /// <see cref="FiltersAreRememberedPerPage"/>). Both pages here already have their own per-page
    /// cookie from <see cref="RememberFiltersPageFilter"/>; this pins the one value (team) that is
    /// meant to leak across that boundary.
    /// </summary>
    [Fact]
    public async Task APickedTeam_CarriesToAnotherTeamFilteredPage()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        var teamId = factory.Seeded.TeamId;

        await client.GetAsync($"{Directory}?teamId={teamId}");
        var otherPage = await client.GetAsync("/SessionManager/UnmatchedPayments");

        Assert.Equal(HttpStatusCode.Redirect, otherPage.StatusCode);
        Assert.Contains($"teamId={teamId}", otherPage.Headers.Location!.ToString());
    }

    /// <summary>
    /// A page with no team filter at all (Audit Log has none) must not pick up a stray, permanently
    /// ignored <c>teamId</c> — see <see cref="RememberFiltersPageFilter"/>'s <c>hasTeamFilter</c> guard.
    /// </summary>
    [Fact]
    public async Task APickedTeam_DoesNotReachAPageWithNoTeamFilter()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        var teamId = factory.Seeded.TeamId;

        await client.GetAsync($"{Directory}?teamId={teamId}");
        var auditLog = await client.GetAsync("/Admin/AuditLog");

        Assert.Equal(HttpStatusCode.OK, auditLog.StatusCode);
    }

    /// <summary>
    /// Explicitly picking "All teams" is itself a remembered choice (empty string, not "nothing
    /// picked") and overrides a more specific team remembered earlier on another page.
    /// </summary>
    [Fact]
    public async Task PickingAllTeams_OverridesAPreviouslyPickedTeamOnAnotherPage()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        var teamId = factory.Seeded.TeamId;

        await client.GetAsync($"{Directory}?teamId={teamId}");
        await client.GetAsync("/SessionManager/UnmatchedPayments?teamId=");

        var backToDirectory = await client.GetAsync(Directory);

        Assert.Equal(HttpStatusCode.Redirect, backToDirectory.StatusCode);
        Assert.DoesNotContain($"teamId={teamId}", backToDirectory.Headers.Location!.ToString());
    }

    /// <summary>
    /// The sessions list keeps its own bespoke cookie (<see cref="TheSessionsListDoesNotOptIn"/>) but
    /// still participates in the shared team cookie — a team applied there must carry to a page using
    /// the general mechanism, the same as between any two of those pages.
    /// </summary>
    [Fact]
    public async Task ATeamAppliedOnTheSessionsList_CarriesToAnotherTeamFilteredPage()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        var teamId = factory.Seeded.TeamId;

        await client.GetAsync($"/SessionManager?applied=true&teamId={teamId}");
        var otherPage = await client.GetAsync("/SessionManager/UnmatchedPayments");

        Assert.Equal(HttpStatusCode.Redirect, otherPage.StatusCode);
        Assert.Contains($"teamId={teamId}", otherPage.Headers.Location!.ToString());
    }

    /// <summary>
    /// The reverse direction: a team picked on a general page reaches the sessions list too, via the
    /// same shared cookie. The sessions list never redirects a bare visit (it restores from its own
    /// cookie in place), so this only proves the read path does not throw — the propagation itself is
    /// exercised more directly by the write-side tests above, which share the same helper.
    /// </summary>
    [Fact]
    public async Task ATeamPickedElsewhere_IsAcceptedByABareVisitToTheSessionsList()
    {
        using var factory = new WebAppFactory();
        var client = RawClient(factory);
        var teamId = factory.Seeded.TeamId;

        await client.GetAsync($"{Directory}?teamId={teamId}");
        var sessions = await client.GetAsync("/SessionManager");

        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
    }
}
