using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A VE's own self-service page must not show their session statistics (Mike, 2026-08-10). The
/// counts are a team's operational view of who is carrying the load — they belong on the admin page,
/// not on the page the VE themselves reads.
///
/// <para>A source scan rather than a rendering test, because the risk is a <b>copy</b>: the two pages
/// look alike, both show a VE's details, and the admin one now has a panel that would look at home on
/// either. Nothing would fail if someone moved it across.</para>
/// </summary>
public class VeSelfServiceShowsNoStatsTests
{
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    [Theory]
    [InlineData("Details.cshtml")]
    [InlineData("Details.cshtml.cs")]
    public void TheSelfServicePageNeverReferencesSessionStatistics(string fileName)
    {
        var path = Path.Combine(RepositoryRoot().FullName,
            "src", "VeSessionManager.Web", "Pages", "VeSelfService", fileName);
        Assert.True(File.Exists(path), $"Expected the self-service page at {path}");

        var text = File.ReadAllText(path);

        foreach (var forbidden in new[] { "SessionHistory", "VolunteerExaminerReportService", "GetPersonSessionHistoryAsync", "stat-row" })
        {
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal),
                $"{fileName} references '{forbidden}'. Session statistics are for the team's admin view of a VE, " +
                $"not for the VE's own page — see this test's remarks.");
        }
    }
}
