using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Every file path named in a *live* document must exist.
///
/// <para><b>Why this is a test and not a habit.</b> A documentation scan on 2026-08-13 found six
/// statements that no longer matched the code, and five were of exactly one shape: a path that had
/// been renamed or deleted. The README pointed at <c>build-and-deploy.yml</c> (the file is
/// <c>deploy.yml</c>) and told the reader the deploy job was "currently a stub" — eighteen releases
/// after it started deploying. <c>icons.md</c> explained a decision in terms of
/// <c>_Layout.cshtml</c>, which does not exist, and a vendored Bootstrap that had been deleted.
/// None of it was caught by anything, because prose has no compiler.</para>
///
/// <para><b>Historical documents are exempt, and that exemption is the whole design.</b>
/// <c>CHANGELOG.md</c> and the audit task lists exist precisely to describe code that no longer
/// exists; asserting their references resolve would be asserting that history is present tense. So
/// the allowlist below is not a workaround for inconvenient failures — it is the line between "this
/// document describes the system" and "this document describes what happened."</para>
///
/// <para>Same family as <see cref="NoNulBytesInSourceTests"/>: a source scan for a mistake that
/// produces no error and no symptom until someone acts on it.</para>
/// </summary>
public class DocumentationReferenceTests
{
    /// <summary>
    /// Documents about the past. A reference that no longer resolves is expected here — it is often
    /// the point of the sentence.
    /// </summary>
    private static readonly string[] HistoricalDocuments =
    [
        "CHANGELOG.md",
        "audit-2026-08-03-tasks.md",
        "audit-2026-08-11-tasks.md",
        "audit-2026-08-11-report.md",
        "security-hardening-2026-07-21.md",
        "security-hardening-2026-08-03.md",
        "fcc-uls-watcher.md",   // the removed bulk-file subsystem, kept for the rules it justifies
        "spec.md"               // the original build plan, written before the code existed
    ];

    /// <summary>
    /// Backticked things that end in a file extension. Deliberately narrow: prose mentions plenty of
    /// names that are not paths, and a scan that cries wolf gets muted.
    /// </summary>
    private static readonly Regex PathReference = new(
        @"`([A-Za-z0-9_./-]+\.(?:cs|cshtml|js|css|json|yml|csproj|props))(?::[\d,\-\s]+)?`",
        RegexOptions.Compiled);

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

    private static IEnumerable<string> LiveDocuments(DirectoryInfo root)
    {
        var candidates = Directory.EnumerateFiles(root.FullName, "*.md", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(root.FullName, "docs"), "*.md", SearchOption.AllDirectories));

        return candidates.Where(f => !HistoricalDocuments.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Endpoint paths that merely look like files. ExamTools' API is the whole list: <c>export/</c>
    /// paths are URL segments on someone else's server, and no amount of path resolution will find
    /// them here.
    /// </summary>
    private static readonly string[] NotFilePaths = ["export/full.json", "export/basic.json"];

    /// <summary>
    /// The docs abbreviate paths — <c>Core/Usd.cs</c> for <c>src/VeSessionManager.Core/Usd.cs</c>,
    /// <c>Worker/Program.cs</c>, <c>/js/app.js</c>. That is good writing, not sloppiness, so a
    /// suffix match counts, and the <c>VeSessionManager.</c> project prefix may be elided. What is
    /// asserted is that the file still exists under this name, not that the doc spells out a full
    /// path.
    ///
    /// <para>Note <c>TrimStart("./")</c> rather than <c>TrimStart('.', '/')</c>: the char-array
    /// overload strips <i>every</i> leading character in the set, which silently turns
    /// <c>.github/workflows/deploy.yml</c> into <c>github/…</c> and reports a file that plainly
    /// exists. That mistake cost two rounds here.</para>
    /// </summary>
    private static bool Resolves(string reference, IReadOnlyCollection<string> repositoryFiles)
    {
        var normalized = reference.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');

        if (NotFilePaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return repositoryFiles.Any(f =>
            f.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("/VeSessionManager." + normalized, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryFilePathNamedInALiveDocumentExists()
    {
        var root = RepositoryRoot();

        var repositoryFiles = Directory
            .EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root.FullName, f).Replace('\\', '/'))
            .ToList();

        var offenders = new List<string>();

        foreach (var document in LiveDocuments(root))
        {
            var text = File.ReadAllText(document);
            foreach (Match match in PathReference.Matches(text))
            {
                var reference = match.Groups[1].Value;
                if (Resolves(reference, repositoryFiles))
                {
                    continue;
                }

                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root.FullName, document).Replace('\\', '/')}:{line} -> {reference}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A live document names a file that does not exist. Either the file moved and the document " +
            "was not updated, or the document is describing history — in which case add it to " +
            $"{nameof(HistoricalDocuments)} rather than deleting the sentence:\n  "
            + string.Join("\n  ", offenders.Distinct()));
    }
}
