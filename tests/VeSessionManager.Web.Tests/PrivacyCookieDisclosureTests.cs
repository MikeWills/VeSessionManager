using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The public privacy page has to name every cookie this app sets (2026-08-24).
///
/// <para>Mike raised the European question when the filter cookie was generalised. The answer was not
/// a banner — every page that sets it is behind <c>[Authorize]</c>, so it only ever reaches signed-in
/// volunteers, and a banner shown after login to people who cannot decline and still use the tool is
/// not consent. The real gap was that the privacy page said nothing about cookies at all.</para>
///
/// <para><b>The list is checked against the source, not hardcoded.</b> A disclosure that is accurate
/// today and silently wrong the next time somebody adds a cookie is worse than none — it is a claim
/// rather than an omission.</para>
/// </summary>
public class PrivacyCookieDisclosureTests
{
    private static string WebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "VeSessionManager.Web")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root.");
        return Path.Combine(directory!.FullName, "src", "VeSessionManager.Web");
    }

    /// <summary>
    /// ⚠️ Every <c>Cookies.Append</c> in the Web project. Framework cookies (sign-in, antiforgery) do
    /// not appear here because the framework writes them — they are disclosed on the page too, but
    /// this scan is what catches a new one <i>we</i> add.
    /// </summary>
    [Fact]
    public void EveryCookieThisAppSetsIsCountedInTheDisclosure()
    {
        var root = WebProjectRoot();
        var writers = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Cookies\.Append\(")
                .Select(_ => Path.GetFileName(f)))
            .ToList();

        // Two today: the sessions list's own filter cookie and the general one. If this number moves,
        // the privacy page's "four cookies" sentence has to move with it.
        Assert.Equal(2, writers.Count);
    }

    [Fact]
    public void ThePrivacyPageDisclosesCookies()
    {
        var page = File.ReadAllText(Path.Combine(WebProjectRoot(), "Pages", "Privacy.cshtml"));

        Assert.Contains("<h2>Cookies</h2>", page);
        Assert.Contains("four cookies", page);

        // The sentence a reader actually wants, and the one that must stay true.
        Assert.Contains("advertising", page);
        Assert.Contains("analytics", page);
    }

    /// <summary>The page is public — a privacy policy nobody can read without an account is not one.</summary>
    [Fact]
    public async Task ThePrivacyPageIsReadableWithoutSigningIn()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Privacy");

        Assert.Contains("Cookies", html);
    }
}
