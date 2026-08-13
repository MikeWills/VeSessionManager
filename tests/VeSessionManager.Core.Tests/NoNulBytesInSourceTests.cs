using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A literal U+0000 byte in a source file is invisible twice over, and both kinds of invisibility
/// have already cost this repo something real (issue #300, 2026-08-11).
///
/// <para><b>It hides the file from search.</b> ripgrep classifies any file containing a NUL as
/// binary and silently reports no matches — not an error, not a warning, just nothing. Two files
/// were affected, and the consequence was not theoretical: a code review searched for references to
/// <c>VeSessionInvitationService</c>, found none because the only caller lives in one of those two
/// files, and recommended deleting its DI registration. Acting on that would have crashed the
/// VE-invite page.</para>
///
/// <para><b>It hides a runtime bug.</b> The NUL in <c>VeInvite.cshtml.cs</c> was the "untagged"
/// filter sentinel. An HTML parser rewrites U+0000 to U+FFFD — whether it arrives raw or as
/// <c>&amp;#x0;</c> — so the value JavaScript read back was never the value C# emitted, the equality
/// test always failed, and choosing "Untagged" hid every VE instead of showing the untagged ones.
/// Nothing threw; the control just quietly did the opposite of its label.</para>
///
/// <para>A byte scan rather than a lint rule, because the character is unprintable: it survives code
/// review by being impossible to see, and survives search by suppressing the search. The only
/// reliable detector is to look at the bytes.</para>
///
/// <para>Same shape and same rationale as <see cref="InlineEventHandlerTests"/> — a source scan for
/// a mistake whose defining property is that it produces no error.</para>
/// </summary>
public class NoNulBytesInSourceTests
{
    /// <summary>
    /// Walks up from the test binary to the repository root — same approach as
    /// <see cref="InlineEventHandlerTests"/>, and it fails with a clear message rather than silently
    /// scanning nothing.
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
    /// Hand-authored text only. Extensions rather than a denylist, so a new binary asset (an icon, a
    /// font, a <c>.woff2</c>) is out of scope by construction rather than by remembering to exclude
    /// it.
    /// </summary>
    private static readonly string[] TextExtensions =
        [".cs", ".cshtml", ".js", ".css", ".json", ".csproj", ".props", ".config", ".md"];

    /// <summary>
    /// Everything hand-authored: <c>src/</c>, <c>docs/</c>, and the top-level markdown beside them
    /// (CLAUDE.md above all).
    ///
    /// <para><b>Documentation was added on 2026-08-13 because it had already happened there.</b>
    /// <c>docs/audit-2026-08-11-tasks.md</c> carried a literal NUL inside the <i>Fix</i> line of
    /// D-01 — the finding about NUL bytes — so the record of this exact trap was itself invisible to
    /// the search that would find it. Worse than a source file would have been: CLAUDE.md points
    /// readers at that document specifically, to learn what not to re-audit and which findings were
    /// wrong, and `grep` answered every question about it with silence.</para>
    /// </summary>
    private static IEnumerable<string> ScannedFiles(DirectoryInfo repositoryRoot)
    {
        foreach (var directoryName in new[] { "src", "docs" })
        {
            var directory = Path.Combine(repositoryRoot.FullName, directoryName);
            Assert.True(Directory.Exists(directory), $"Expected {directoryName} at {directory}");

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        foreach (var file in Directory.EnumerateFiles(repositoryRoot.FullName, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }
    }

    /// <summary>
    /// <c>wwwroot/lib</c> is vendored third-party code we do not author and would not fix here;
    /// <c>bin</c>/<c>obj</c> are build output. Everything else under src/ is ours.
    /// </summary>
    private static bool IsExcluded(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains(Path.Combine("wwwroot", "lib"), StringComparison.Ordinal);

    [Fact]
    public void NoSourceOrDocumentationFileContainsANulByte()
    {
        var repositoryRoot = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in ScannedFiles(repositoryRoot))
        {
            if (IsExcluded(file) || !TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(file);
            var index = Array.IndexOf(bytes, (byte)0);
            if (index >= 0)
            {
                // Report the line so the offender is findable -- by hand, since search cannot see it.
                var line = bytes.Take(index).Count(b => b == (byte)'\n') + 1;
                offenders.Add($"{Path.GetRelativePath(repositoryRoot.FullName, file)}:{line} (byte offset {index})");
            }
        }

        Assert.True(offenders.Count == 0,
            "A NUL byte makes a file binary to ripgrep, so every search silently skips it — and if the byte sits " +
            "in a string literal that reaches HTML, the browser rewrites it to U+FFFD and the value stops " +
            "round-tripping. Both happened in issue #300. Use a printable sentinel instead:\n  "
            + string.Join("\n  ", offenders));
    }
}
