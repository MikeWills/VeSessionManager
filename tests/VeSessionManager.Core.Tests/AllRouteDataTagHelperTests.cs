using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>asp-all-route-data</c> and <c>asp-route-*</c> must never appear on the same tag.
///
/// <para><b>They fight rather than merge.</b> Both feed the tag helper's single RouteValues
/// dictionary, but the dictionary attribute <i>assigns</i> it while a prefixed attribute <i>adds</i>
/// one entry — so whichever the generated code reaches last wins, which is decided by attribute
/// order in the markup.</para>
///
/// <para><b>This shipped (2026-08-10).</b> The VE Directory's row links had
/// <c>asp-route-id</c> followed by <c>asp-all-route-data</c>, so every link to a VE was rendered
/// with the filters and <b>no id</b>. Nothing threw — the anchor rendered, the href looked
/// plausible, and the detail page simply could not find the VE. A silent wrong answer, which is
/// worse than the render-time exception <see cref="FormActionTagHelperTests"/> guards, because only
/// a human clicking the link finds it.</para>
///
/// <para>The fix is one dictionary containing everything, built in the page model — see
/// <c>VeDirectoryModel.DetailRoute</c>.</para>
/// </summary>
public class AllRouteDataTagHelperTests
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

    /// <summary>Any opening tag, however many lines it wraps over.</summary>
    private static readonly Regex AnyTag = new(@"<\w[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex AllRouteData = new(@"\basp-all-route-data\s*=", RegexOptions.Compiled);
    private static readonly Regex SingleRouteValue = new(@"\basp-route-[\w-]+\s*=", RegexOptions.Compiled);

    [Fact]
    public void NoTagCombinesAllRouteDataWithIndividualRouteValues()
    {
        var pagesDirectory = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Web", "Pages");
        Assert.True(Directory.Exists(pagesDirectory), $"Expected Razor Pages at {pagesDirectory}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match tag in AnyTag.Matches(text))
            {
                if (!AllRouteData.IsMatch(tag.Value) || !SingleRouteValue.IsMatch(tag.Value))
                {
                    continue;
                }

                var line = text.Take(tag.Index).Count(c => c == '\n') + 1;
                var attribute = SingleRouteValue.Match(tag.Value).Value.TrimEnd('=', ' ');
                offenders.Add($"{Path.GetRelativePath(pagesDirectory, file)}:{line} — asp-all-route-data beside {attribute}");
            }
        }

        Assert.True(offenders.Count == 0,
            "asp-all-route-data ASSIGNS the tag helper's route dictionary while asp-route-* ADDS to it, so one " +
            "silently discards the other depending on attribute order — the link still renders, just with a " +
            "value missing. Put everything in one dictionary built in the page model instead:\n  " +
            string.Join("\n  ", offenders));
    }
}
