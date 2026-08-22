using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <b>The guard, rather than the proof.</b> <see cref="TeamDeletionSqliteTests"/> shows the delete
/// works against the tables that exist today; this reads the EF model instead, so it starts failing
/// the moment somebody adds a table that points at <see cref="Team"/> and does not teach
/// <c>TeamDeletionService.DeleteAsync</c> about it.
///
/// <para>That is the realistic way this breaks. A team delete is written once, and the tables it has
/// to walk keep arriving — several of the sixteen it handles did not exist two months ago. A
/// hand-written list of assertions cannot notice the seventeenth, and the symptom is not a compile
/// error: it is a <c>Restrict</c> foreign key throwing partway through the one action nobody can
/// retry, or rows quietly left pointing at a team id that no longer exists.</para>
///
/// <para><b>Asked through the model's own foreign keys and DbSet names</b>, not by pluralising type
/// names. The first version of this test guessed <c>JobRunHistory</c> + <c>"s"</c>, decided
/// <c>JobRunHistories</c> was unhandled, and reported a table that was handled all along — a guard
/// that cries wolf gets muted, which is worse than not having it.</para>
/// </summary>
public class TeamDeletionCoverageTests
{
    /// <summary>
    /// Tables pointing at Team that the delete deliberately leaves for something else, each with the
    /// reason. Anything else must be named in the service, or this test fails.
    /// </summary>
    private static readonly Dictionary<string, string> HandledElsewhere = new(StringComparer.Ordinal)
    {
        // Removed by TeamId like the rest, but the deletion's own entry is written with TeamId null
        // so that sweep cannot reach it. Named here because the service refers to it as AuditLogs
        // through AddAuditLog as well as the delete, and the distinction is worth stating once.
        ["AuditLog"] = "cleared by TeamId; the deletion's own entry is deliberately team-less"
    };

    private static AppDbContext CreateContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
    }

    private static string ServiceSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot().FullName, "src", "VeSessionManager.Core", "Admin", "TeamDeletionService.cs"));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>
    /// Entity CLR type name to the <c>DbSet</c> property that exposes it — read off the context so
    /// irregular plurals (<c>JobRunHistories</c>, <c>CandidateUlsHistoryEntries</c>) are exact rather
    /// than guessed.
    /// </summary>
    private static Dictionary<string, string> DbSetNamesByEntity() =>
        typeof(AppDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.PropertyType.GetGenericArguments()[0].Name, p => p.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryTablePointingAtATeam_IsNamedByTheDelete()
    {
        using var dbContext = CreateContext();
        var source = ServiceSource();
        var dbSetNames = DbSetNamesByEntity();

        // Real foreign keys into Team, rather than "has a property called TeamId" — a differently
        // named or shadow FK would slip straight past the property check.
        var pointingAtTeam = dbContext.Model.GetEntityTypes()
            .Where(e => e.GetForeignKeys().Any(fk => fk.PrincipalEntityType.ClrType == typeof(Team)))
            .Select(e => e.ClrType.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !HandledElsewhere.ContainsKey(name))
            .ToList();

        var unhandled = pointingAtTeam
            .Where(name => !dbSetNames.TryGetValue(name, out var dbSet)
                        || !source.Contains("dbContext." + dbSet, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(unhandled.Count == 0,
            "These tables have a foreign key into Team but TeamDeletionService never mentions them, so deleting a "
            + "team would either throw on a Restrict foreign key or leave rows pointing at a team that no longer exists:\n  "
            + string.Join("\n  ", unhandled)
            + "\n\nAdd them to DeleteAsync in dependency order (leaves first), or to HandledElsewhere with a reason.");
    }

    /// <summary>
    /// The counterpart, and the one that catches a rename rather than an addition: an exemption for a
    /// table that no longer exists is an exemption outliving its reason, and would silently excuse a
    /// future table that happened to take the same name.
    /// </summary>
    [Fact]
    public void NoExemptionOutlivesItsTable()
    {
        using var dbContext = CreateContext();
        var known = dbContext.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet(StringComparer.Ordinal);

        var stale = HandledElsewhere.Keys
            .Where(name => !known.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "TeamDeletionCoverageTests excuses entity types that no longer exist:\n  " + string.Join("\n  ", stale));
    }
}
