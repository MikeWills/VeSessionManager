using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Everything under the nav's <b>Reports</b> group is admin-gated (Mike, 2026-08-15) — a standing
/// rule, not a property of the three pages that happen to be in there today.
///
/// <para>These pages each surface a per-VE session count, and a visible count-per-person invites
/// comparison between volunteers that nobody asked for. That reasoning is already recorded on
/// <c>VeRoster</c> and <c>Stats</c> individually; what was missing was anything stopping a fourth
/// report being added later without it.</para>
///
/// <para><b>The nav is read out of the layout markup rather than hardcoded here</b>, so adding a link
/// to the group is what pulls it into this test. A hardcoded list would pass forever while the real
/// menu grew — the same "two copies agreeing right until someone edits one" shape that
/// <c>ActionMessageSingleSourceTests</c> exists to catch, and it is why that one is a source scan
/// too.</para>
/// </summary>
public class ReportsNavGateTests
{
    private const string LayoutPath = "Pages/Shared/_AppLayout.cshtml";

    /// <summary>
    /// Every <c>asp-page</c> inside the Reports menu block. Scoped by locating the group's own
    /// trigger button and reading to the end of its menu, so links in neighbouring groups are not
    /// swept in.
    /// </summary>
    public static IEnumerable<object[]> ReportPages()
    {
        var layout = File.ReadAllText(Path.Combine(WebProjectRoot(), LayoutPath));

        var start = layout.IndexOf(">Reports <", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "Could not find the Reports nav group in the layout. If it was renamed, update this test — do not delete it.");

        var menuEnd = layout.IndexOf("</div>", layout.IndexOf("<div class=\"menu\">", start, StringComparison.Ordinal), StringComparison.Ordinal);
        var block = layout[start..menuEnd];

        var matches = Regex.Matches(block, @"asp-page=""(?<page>[^""]+)""");
        Assert.NotEmpty(matches);

        foreach (Match match in matches)
        {
            yield return [match.Groups["page"].Value];
        }
    }

    /// <summary>
    /// The rule itself: a page reachable from Reports requires the SystemAdmin/TeamAdmin roles.
    ///
    /// <para>Asserted on the endpoint's authorization metadata rather than by probing status codes —
    /// a 401/403 probe cannot tell "denied by role" from "denied for some other reason", the same
    /// trap recorded for the Square webhook's anonymous-access test.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ReportPages))]
    public void EveryPageInTheReportsMenuRequiresAnAdminRole(string page)
    {
        using var factory = new WebAppFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var endpoint = endpoints.OfType<RouteEndpoint>().FirstOrDefault(e =>
            string.Equals("/" + (e.RoutePattern.RawText ?? "").TrimStart('/'), page, StringComparison.OrdinalIgnoreCase));
        Assert.True(endpoint is not null, $"Reports menu links to {page}, which is not a routable page.");

        var authorizeData = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Assert.True(authorizeData.Count > 0, $"{page} is linked from Reports but carries no [Authorize] at all.");

        var roles = authorizeData
            .Select(a => a.Roles)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        Assert.True(roles is not null, $"{page} is linked from Reports but is not role-gated.");

        var allowed = roles!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("SystemAdmin", allowed);
        Assert.Contains("TeamAdmin", allowed);

        // The two that must never reach a report. SessionManager is the interesting one: it is the
        // role a report would most plausibly be opened up to by accident, since it can already see
        // sessions and candidates.
        Assert.DoesNotContain("SessionManager", allowed);
        Assert.DoesNotContain("TeamLead", allowed);
    }

    /// <summary>Walks up from the test binary to the repo root, then into the Web project — same approach as the other source-scanning tests here.</summary>
    private static string WebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "VeSessionManager.Web")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(directory!.FullName, "src", "VeSessionManager.Web");
    }
}
