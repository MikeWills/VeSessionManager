using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A <c>&lt;form&gt;</c> may carry an explicit <c>action=</c> or the routing tag helpers, never both.
/// <c>FormTagHelper</c> throws <c>InvalidOperationException</c> when it sees both — <b>at render
/// time</b>, which is the problem.
///
/// <para><b>This shipped to a deployment (2026-08-10).</b> The VE Directory's add-VE form had
/// <c>asp-page-handler</c> beside an explicit <c>action</c>; it compiled, the whole suite passed, and
/// the page 500'd the first time anyone loaded it. Nothing in this repo renders Razor, so the build
/// cannot see it and neither can a service-level test.</para>
///
/// <para>The combination is easy to reach honestly, because the fix for a <i>different</i> documented
/// trap points straight at it: <c>asp-page-handler</c> builds the action from the route alone and
/// drops the query string, so a filtered list page needs an explicit <c>action</c> — and the natural
/// edit is to add one while leaving the handler attribute in place. The handler name belongs in the
/// <c>Url.Page</c> call instead.</para>
/// </summary>
public class FormActionTagHelperTests
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

    /// <summary>An opening form tag, across however many lines it is wrapped over.</summary>
    private static readonly Regex FormTag = new(@"<form\b[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The attributes FormTagHelper refuses to see beside an explicit action, per its own error message.</summary>
    private static readonly Regex RoutingAttribute = new(
        @"\basp-(route-[\w-]+|action|controller|fragment|area|route|page|page-handler)\s*=",
        RegexOptions.Compiled);

    private static readonly Regex ExplicitAction = new(@"(?<![\w-])action\s*=", RegexOptions.Compiled);

    [Fact]
    public void NoFormCombinesAnExplicitActionWithRoutingTagHelpers()
    {
        var pagesDirectory = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Web", "Pages");
        Assert.True(Directory.Exists(pagesDirectory), $"Expected Razor Pages at {pagesDirectory}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match form in FormTag.Matches(text))
            {
                if (!ExplicitAction.IsMatch(form.Value) || !RoutingAttribute.IsMatch(form.Value))
                {
                    continue;
                }

                var line = text.Take(form.Index).Count(c => c == '\n') + 1;
                var attribute = RoutingAttribute.Match(form.Value).Value.TrimEnd('=', ' ');
                offenders.Add($"{Path.GetRelativePath(pagesDirectory, file)}:{line} — action= beside {attribute}");
            }
        }

        Assert.True(offenders.Count == 0,
            "FormTagHelper throws at render time when a <form> has both an explicit action= and a routing " +
            "tag helper, so the page 500s rather than failing the build. Put the handler name in the Url.Page " +
            "call and drop the asp-* attribute (keep asp-antiforgery, which is not a routing attribute):\n  " +
            string.Join("\n  ", offenders));
    }
}
