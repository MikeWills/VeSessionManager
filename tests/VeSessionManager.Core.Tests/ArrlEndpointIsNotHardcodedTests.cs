using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A source scan proving nothing in this repository can post to ARRL by accident (issue #197).
///
/// <para><b>Why a test and not care.</b> Every other integration here can be exercised safely: Square
/// has a sandbox, ExamTools has a dev site, email has a test-mode redirect. ARRL has none — there is
/// no staging endpoint and no dry-run, so the first exercise of the real path files a real session
/// with the organization that issues licenses, on behalf of a team whose reputation is attached to
/// it. "Be careful" is not a mechanism; this is.</para>
///
/// <para>The endpoint therefore lives in configuration and is <b>blank in the shipped
/// <c>appsettings.json</c></b>, so a fresh clone, a developer machine and the test suite have nowhere
/// to post. Only <c>appsettings.Production.json</c> carries the real URL — it is not a secret, so it
/// commits and deploys like any other setting.</para>
///
/// <para>Same shape as <c>NoNulBytesInSourceTests</c>: a source scan that fails the build when a
/// dangerous thing reappears, rather than a convention someone has to remember.</para>
/// </summary>
public class ArrlEndpointIsNotHardcodedTests
{
    /// <summary>
    /// Matched loosely on the host so a variant path, scheme or subdomain cannot slip past.
    ///
    /// <para><b>Split so this file does not match its own rule.</b> Written as one literal, the scan
    /// reports itself — which was the first thing it did. Exempting this file by name would have
    /// worked and would have left a hole exactly where the rule is defined.</para>
    /// </summary>
    private const string ArrlHost = "arrl" + ".org";

    private static readonly string[] ScannedRoots = ["src", "tests"];

    /// <summary>
    /// The one file allowed to name it. Production config is where the real endpoint belongs, and
    /// deployment settings are exactly the thing an operator is expected to review.
    /// </summary>
    private static readonly string[] AllowedFiles = ["appsettings.Production.json"];

    /// <summary>Same walk-up as the other source-scanning tests in this project.</summary>
    private static string RepositoryRootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRootPath();
        foreach (var scanned in ScannedRoots)
        {
            var directory = Path.Combine(root, scanned);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var pattern in new[] { "*.cs", "*.cshtml", "*.json" })
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
                {
                    // bin/obj hold copies of the very config files this is about.
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// No source file, test, fixture or non-production config may name ARRL's host. A test pointing at
    /// the real endpoint is the specific accident this prevents: it would file a fabricated session
    /// with a real VEC, on every CI run, and the first anyone would know of it is ARRL asking about it.
    /// </summary>
    [Fact]
    public void ArrlsHostAppearsOnlyInProductionConfiguration()
    {
        var offenders = SourceFiles()
            .Where(file => !AllowedFiles.Contains(Path.GetFileName(file)))
            .Where(file => File.ReadAllText(file).Contains(ArrlHost, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(RepositoryRootPath(), file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "ARRL's upload endpoint must stay in configuration, and blank outside production — every "
            + "POST to it files a real session with a real VEC and cannot be undone. Found it in:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The shipped default must stay empty. A real URL here would make every developer machine, every
    /// test run and every fresh clone capable of filing a session — which is the whole thing this
    /// arrangement exists to prevent.
    /// </summary>
    [Fact]
    public void TheShippedDefaultUploadUrlIsBlank()
    {
        var appsettings = Path.Combine(
            RepositoryRootPath(), "src", "VeSessionManager.Web", "appsettings.json");

        var text = File.ReadAllText(appsettings);

        Assert.DoesNotContain(ArrlHost, text, StringComparison.OrdinalIgnoreCase);
    }
}
