using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The Web app's CSP sets <c>script-src 'self'</c>, which means an inline event handler attribute
/// (<c>onchange=</c>, <c>onclick=</c>, …) is <b>silently dropped by the browser</b>.
///
/// <para>That silence is the whole problem. The markup reads correctly, the control renders
/// correctly, and nothing fails — the handler just never runs, and only the browser console says
/// so. Two shipped that way before anyone noticed (2026-08-09): the VE Directory's "Show retired"
/// checkbox, and the session list's page-size picker. Both looked finished and neither did
/// anything when clicked. The directory one was reported as a <i>styling</i> complaint, which is
/// how long a dead control can sit there being used.</para>
///
/// <para>A source scan rather than a browser test, because the mistake is someone <i>writing</i> the
/// attribute — the natural, obvious spelling for anyone who hasn't read the CSP. Use app.js's
/// delegated <c>data-autosubmit</c>, or add a delegated listener there, instead.</para>
/// </summary>
public class InlineEventHandlerTests
{
    /// <summary>
    /// Walks up from the test binary to the repository root — same approach as
    /// <see cref="VeTagsGrantNoAccessTests"/>, and it fails with a clear message rather than
    /// silently scanning nothing.
    /// </summary>
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

    /// <summary>
    /// An <c>on…=</c> attribute on a tag. Anchored on a preceding space or quote so it can't match
    /// the middle of a word, and requires an <c>=</c> immediately after the name so prose like
    /// "replaces a &lt;select onchange=submit&gt;" inside a Razor comment isn't reported — comments
    /// are stripped first anyway, but the app.css/app.js docs discuss these by name too.
    /// </summary>
    private static readonly Regex InlineHandler = new(
        """[\s"'](on[a-z]+)\s*=\s*["']""", RegexOptions.Compiled);

    /// <summary>Razor <c>@* … *@</c> and HTML <c>&lt;!-- … --&gt;</c> comments, which legitimately discuss inline handlers.</summary>
    private static readonly Regex Comments = new(
        @"@\*.*?\*@|<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void NoRazorPageUsesAnInlineEventHandlerAttribute()
    {
        var pagesDirectory = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Web", "Pages");
        Assert.True(Directory.Exists(pagesDirectory), $"Expected Razor Pages at {pagesDirectory}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories))
        {
            var lines = Comments.Replace(File.ReadAllText(file), "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var match = InlineHandler.Match(lines[i]);
                if (match.Success)
                {
                    offenders.Add($"{Path.GetRelativePath(pagesDirectory, file)}:{i + 1} — {match.Groups[1].Value}=");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Inline event handlers are dead on arrival under the app's `script-src 'self'` CSP — the browser " +
            "drops them and the control silently does nothing. Use app.js's data-autosubmit, or add a delegated " +
            "listener there:\n  " + string.Join("\n  ", offenders));
    }
}
