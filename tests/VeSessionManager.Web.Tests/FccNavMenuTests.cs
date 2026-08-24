using System.Text.RegularExpressions;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The nav's <b>FCC</b> group (#458) — three public FCC pages every VE ends up on.
///
/// <para><b>Read out of the layout rather than hardcoded here</b>, the same way
/// <see cref="ReportsNavGateTests"/> works, so a fourth link added later is pulled into these rules
/// instead of quietly escaping them. A hardcoded list passes forever while the real menu grows.</para>
/// </summary>
public class FccNavMenuTests
{
    private const string LayoutPath = "Pages/Shared/_AppLayout.cshtml";

    private static string FccMenuBlock()
    {
        var layout = File.ReadAllText(Path.Combine(WebProjectRoot(), LayoutPath));

        var start = layout.IndexOf(">FCC <", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "Could not find the FCC nav group in the layout. If it was renamed, update this test — do not delete it.");

        var menuStart = layout.IndexOf("<div class=\"menu\">", start, StringComparison.Ordinal);
        var menuEnd = layout.IndexOf("</div>", menuStart, StringComparison.Ordinal);
        return layout[menuStart..menuEnd];
    }

    public static IEnumerable<object[]> FccLinks() =>
        Regex.Matches(FccMenuBlock(), "<a [^>]*>")
            .Select(m => new object[] { m.Value });

    /// <summary>
    /// ⚠️ Every one of these leaves the app, and losing an in-progress session screen to a licence
    /// lookup is the whole reason people keep these open in separate tabs.
    ///
    /// <para><c>rel="noopener noreferrer"</c> rides along with <c>target="_blank"</c>: without
    /// <c>noopener</c> the opened page gets a handle back to this one through <c>window.opener</c>,
    /// which is a redirect it should not have. Same pairing as the Support link.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(FccLinks))]
    public void EveryFccLinkOpensInANewTab_AndDoesNotHandOverTheOpener(string anchor)
    {
        Assert.Contains("target=\"_blank\"", anchor);
        Assert.Contains("noopener", anchor);
        Assert.Contains("noreferrer", anchor);
    }

    /// <summary>These are the plain search pages, not deep links — see the layout's own note on why.</summary>
    [Theory]
    [InlineData("https://wireless2.fcc.gov/UlsApp/ApplicationSearch/searchAppl.jsp")]
    [InlineData("https://wireless2.fcc.gov/UlsApp/UlsSearch/searchLicense.jsp")]
    [InlineData("https://apps.fcc.gov/cores/userLogin.do")]
    public void TheThreeRequestedDestinationsArePresent(string url) =>
        Assert.Contains(url, FccMenuBlock());

    /// <summary>
    /// Ungated on purpose: these are public FCC pages, and a VE at any level uses them. TeamLead is
    /// the one that matters — it is the role most often left out of a nav group by accident, and the
    /// one that has already been handed a link straight to a 403 once (Unmatched Payments).
    /// </summary>
    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task TheFccMenuRendersForEveryRole(UserRole role)
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(role);

        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Contains("https://apps.fcc.gov/cores/userLogin.do", html);
        Assert.Contains("https://wireless2.fcc.gov/UlsApp/UlsSearch/searchLicense.jsp", html);
    }

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
