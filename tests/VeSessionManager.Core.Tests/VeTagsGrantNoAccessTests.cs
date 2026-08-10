using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #142 promises that VE tags carry no authorization: several of the starting names ("admin",
/// "session manager", "team lead") deliberately match real roles in this app's access model, because
/// those are the words the team already uses, and a VE tagged "admin" must still get nothing from it.
///
/// <para>That is a promise about code nobody has written yet, which makes it exactly the kind that
/// erodes — the first time an authorization check could conveniently read a tag, it will, and the
/// screens will still be telling everyone the tags are decorative. This test is a source scan
/// rather than a reflection check because the failure it guards against is someone *adding* the
/// dependency, and a scan says plainly which file did it.</para>
/// </summary>
public class VeTagsGrantNoAccessTests
{
    /// <summary>
    /// Walks up from the test binary to the repository root. Fragile-looking, but the alternative —
    /// a hard-coded relative depth — breaks on any change to the output path, and this at least
    /// fails with a clear message rather than silently scanning nothing.
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

    [Fact]
    public void NoAuthorizationCodeReadsVeTags()
    {
        var authorizationDirectory = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Core", "Authorization");
        Assert.True(Directory.Exists(authorizationDirectory), $"Expected authorization code at {authorizationDirectory}");

        var offenders = Directory.EnumerateFiles(authorizationDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("VeTag", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "VE tags must never influence authorization — issue #142 states they carry no access, and every screen " +
            "showing them says so. These authorization files reference VeTag: " + string.Join(", ", offenders) +
            ". If a genuine role is needed, add it to UserRole and the access scopes, not to the tag vocabulary.");
    }

    /// <summary>
    /// The same promise from the other direction: the tag entity must not grow a field that looks
    /// like a permission. A "CanApproveSessions" flag on a tag would be honoured by whoever added it
    /// long before anyone updated the "reporting only" copy on three screens.
    /// </summary>
    [Fact]
    public void VeTagHasNoPermissionShapedProperties()
    {
        var suspicious = typeof(Core.Entities.VeTag).GetProperties()
            .Select(p => p.Name)
            .Where(name =>
                name.StartsWith("Can", StringComparison.Ordinal)
                || name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Role", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Access", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(suspicious.Count == 0,
            "VeTag gained a permission-shaped property: " + string.Join(", ", suspicious) +
            ". Tags are reporting labels; access belongs to UserRole.");
    }
}
