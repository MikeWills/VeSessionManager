using System.Text.RegularExpressions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The audit log is append-only, and this is what that claim rests on (#313 / L-06).
///
/// <para><b>It is a convention enforced by absence, not by the database.</b> There is no trigger and
/// no hash chain — anyone with the SQLite file can rewrite history and nothing would show. That is
/// an accepted position for this threat model (the person who could do it owns the box), and it is
/// written down in docs/audit-log.md so "append-only" is never read as a stronger guarantee than it
/// is.</para>
///
/// <para>What this test protects is the half that <i>is</i> real: no code path in <c>src/</c>
/// updates or deletes an audit row. That property was verified once by hand during the 2026-08-11
/// audit, and a verified-once property is exactly the kind that decays — a plausible-looking
/// "clean up old audit entries" method would restore the gap without anyone noticing it had been a
/// property at all.</para>
///
/// <para>A source scan, because there is nothing to observe at runtime: the absence of a delete path
/// cannot be asserted by calling anything. Same shape as NoNulBytesInSourceTests and
/// ActionMessageSingleSourceTests.</para>
/// </summary>
public class AuditLogAppendOnlyTests
{
    /// <summary>
    /// Ways to mutate or remove rows that would defeat append-only. <c>Add</c> is the one permitted
    /// verb and is deliberately absent.
    /// </summary>
    private static readonly string[] ForbiddenOperations =
    [
        "AuditLogs.Remove",
        "AuditLogs.RemoveRange",
        "AuditLogs.ExecuteDelete",
        "AuditLogs.ExecuteDeleteAsync",
        "AuditLogs.ExecuteUpdate",
        "AuditLogs.ExecuteUpdateAsync",
        "AuditLogs.Update",
        "AuditLogs.UpdateRange",
        "AuditLogs.AddOrUpdate"
    ];

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>Repo-relative and forward-slashed, so a failure message reads the same on either OS.</summary>
    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// The sanctioned delete paths, exempted by filename — <b>two</b> now, each the result of an
    /// explicit decision rather than of code appearing.
    ///
    /// <para>docs/audit-log.md called the first one before it was built: retention "becomes the first
    /// legitimate delete path, and AuditLogAppendOnlyTests will fail. That is the intended behaviour.
    /// The fix at that point is to make the deletion explicit and narrow, and to update this document
    /// — <b>not to widen the test until it passes</b>." Both entries below followed that: named
    /// files, not a relaxed pattern, so a <i>third</i> delete path anywhere still fails.</para>
    ///
    /// <list type="bullet">
    /// <item><c>RecordRetentionService</c> (#86) — deletes on an admin-configured window that is null,
    /// meaning keep everything, by default.</item>
    /// <item><c>UserManagementService</c> (#188) — deletes an account's own lifecycle rows when the
    /// account itself is deleted. Narrow in a way worth stating: an account that acted on anything
    /// else is refused outright, so this cannot erase a record of what somebody did.</item>
    /// </list>
    ///
    /// <para>Moving or renaming either file fails the test too. That is deliberate: it forces whoever
    /// moves it to come here and re-affirm the exemption rather than carrying it along silently.</para>
    /// </summary>
    private static readonly string[] SanctionedDeleteFiles =
    [
        "RecordRetentionService.cs",
        "UserManagementService.cs"
    ];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot().FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void NothingInSourceUpdatesOrDeletesAnAuditRow()
    {
        var root = RepositoryRoot().FullName;
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (SanctionedDeleteFiles.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var operation in ForbiddenOperations.Where(text.Contains))
            {
                offenders.Add(Relative(root, file) + " -> " + operation);
            }
        }

        Assert.True(offenders.Count == 0,
            "The audit log is append-only apart from the sanctioned paths below, and these would break that:\n  "
            + string.Join("\n  ", offenders) +
            "\n\nThe only exemptions are " + string.Join(" and ", SanctionedDeleteFiles) +
            ". A further delete path needs a decision and a documented position first — not a delete call " +
            "added alongside other work. See docs/audit-log.md.");

        // An exemption for a file that no longer deletes anything is an open door left standing after
        // the room behind it was demolished. If retention is ever removed, this fails and the
        // exemption goes with it.
        foreach (var name in SanctionedDeleteFiles)
        {
            var sanctioned = SourceFiles().SingleOrDefault(f => Path.GetFileName(f) == name);
            Assert.True(sanctioned is not null, $"{name} is exempted above but no longer exists — drop the exemption.");
            Assert.Contains("AuditLogs", File.ReadAllText(sanctioned!), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other half: exactly one place constructs an audit row. Several independent
    /// <c>new AuditLog { … }</c> sites is how the fields drift — and it is the state this codebase
    /// was already in once, before AuditLogExtensions replaced a private AddAudit re-declared across
    /// nine services.
    /// </summary>
    [Fact]
    public void OnlyAuditLogExtensionsConstructsAnAuditRow()
    {
        var root = RepositoryRoot().FullName;
        var pattern = new Regex(@"new\s+AuditLog\s*[{(]", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (Path.GetFileName(file) == "AuditLogExtensions.cs")
            {
                continue;
            }

            if (pattern.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Relative(root, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "AuditLog rows must be built through AuditLogExtensions.AddAuditLog so every row carries the same " +
            "fields. These construct one directly:\n  " + string.Join("\n  ", offenders));
    }
}
