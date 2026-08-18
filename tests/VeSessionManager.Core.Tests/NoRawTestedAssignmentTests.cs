using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>Candidate.Tested</c> must be set through <c>MarkTested</c>, never assigned directly (#401 PR3).
///
/// <para><b>Why a source scan rather than trusting review.</b> The bool and its new
/// <c>TestedUtc</c> timestamp have to move together: a site that sets one and forgets the other
/// leaves a candidate the <c>CandidateTested</c> trigger can never see. Nothing throws, nothing logs,
/// no test of that site fails — the candidate simply never gets the email, and the only way to
/// notice is for somebody to ask why. That is the same "produces no error" property that earned
/// <see cref="NoNulBytesInSourceTests"/> and <c>InlineEventHandlerTests</c> their own scans.</para>
///
/// <para>There were four assignment sites when this was written, which is also the argument: a rule
/// somebody has to remember at four places is one that will be forgotten at the fifth.</para>
/// </summary>
public partial class NoRawTestedAssignmentTests
{
    /// <summary>Walks up from the test binary to the repository root — same approach as the other source scans, failing clearly rather than silently scanning nothing.</summary>
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
    /// Matches setting the flag — <c>candidate.Tested = true</c>, <c>c.Tested = false</c> — and
    /// nothing else.
    ///
    /// <para><b>Bound to a boolean literal deliberately.</b> A first version matched any
    /// <c>Tested =</c> and immediately caught <c>SessionStatsService</c>'s
    /// <c>Tested = s.Candidates.Count(…)</c>, which is a stats DTO's own property and has nothing to
    /// do with the candidate flag. A scan that cries wolf gets suppressed, and the shape actually
    /// worth catching is somebody writing the flag by hand.</para>
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])(?:\w+\.)?Tested\s*=\s*(?:true|false)\b", RegexOptions.Compiled)]
    private static partial Regex TestedAssignment();

    /// <summary>The declaration and the helper itself, which are the two places that legitimately touch it.</summary>
    private static bool IsTheDefinitionItself(string path) =>
        Path.GetFileName(path).Equals("Candidate.cs", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void NothingUnderSrcAssignsTestedDirectly()
    {
        var root = RepositoryRoot();
        var source = Path.Combine(root.FullName, "src");
        Assert.True(Directory.Exists(source), $"Expected src at {source}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                || IsTheDefinitionItself(file))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments describe the behaviour ("bulk-flips Candidate.Tested = true") and are not
                // the behaviour.
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TestedAssignment().IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Candidate.Tested must be set through Candidate.MarkTested(now), which also stamps TestedUtc — "
            + "a raw assignment leaves the CandidateTested trigger unable to ever see that candidate:\n"
            + string.Join("\n", offenders));
    }
}
