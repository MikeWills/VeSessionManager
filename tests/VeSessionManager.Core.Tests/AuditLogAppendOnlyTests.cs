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
            var text = File.ReadAllText(file);
            foreach (var operation in ForbiddenOperations.Where(text.Contains))
            {
                offenders.Add(Relative(root, file) + " -> " + operation);
            }
        }

        Assert.True(offenders.Count == 0,
            "The audit log is append-only, and these would break that:\n  " + string.Join("\n  ", offenders) +
            "\n\nIf audit retention is genuinely wanted (see #86), it needs a decision and a documented " +
            "position first — not a delete call added alongside other work. See docs/audit-log.md.");
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
