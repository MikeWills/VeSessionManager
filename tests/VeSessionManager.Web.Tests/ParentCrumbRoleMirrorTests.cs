using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Every <c>ParentCrumb</c>'s role list must match the <c>[Authorize]</c> on the page it links to
/// (#307, DUP-07).
///
/// <para><b>Why this needs enforcing rather than reviewing.</b> The crumb decides whether to render a
/// link to its parent list, and it does that from a role string typed out beside it in the
/// <c>.cshtml</c>. Nothing connects that string to the parent page's actual attribute, so the two can
/// drift silently in either direction — and both directions are wrong in a way nobody would notice
/// from the page itself:</para>
/// <list type="bullet">
///   <item>too permissive: a viewer is offered a breadcrumb that lands them on a 403;</item>
///   <item>too restrictive: a viewer entitled to the parent never sees the link, and the page simply
///         looks like it has no parent.</item>
/// </list>
///
/// <para>Using <c>RoleGroups</c> constants at both ends narrows the gap but does not close it —
/// nothing stops someone writing <c>RoleGroups.SystemAdminOnly</c> on a crumb whose parent admits
/// admins. This closes it.</para>
/// </summary>
public class ParentCrumbRoleMirrorTests
{
    /// <summary>
    /// Matches the crumb's page path and its role argument, which by now is a <c>RoleGroups</c>
    /// constant but is allowed to be a literal so a future call site is caught rather than skipped.
    /// </summary>
    private static readonly Regex CrumbPattern = new(
        """new ParentCrumb\(\s*"\.\/(?<page>[A-Za-z0-9_]+)"\s*,.*?,\s*(?<roles>RoleGroups\.[A-Za-z]+|"[^"]*")\s*\)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>Resolves <c>RoleGroups.Admins</c> to its value; passes a literal through.</summary>
    private static string ResolveRoles(string expression)
    {
        if (expression.StartsWith('"'))
        {
            return expression.Trim('"');
        }

        var name = expression["RoleGroups.".Length..];
        var field = typeof(RoleGroups).GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"RoleGroups.{name} does not exist.");
        return (string)field.GetRawConstantValue()!;
    }

    private static string? AuthorizeRolesOn(string pageName)
    {
        // Razor page models are named <Page>Model and live under Pages.Admin for every current crumb
        // target. Searched across the assembly rather than assumed, so a moved page fails loudly here
        // instead of being silently skipped.
        var type = typeof(RoleGroups).Assembly.GetTypes()
            .SingleOrDefault(t => t.Name == pageName + "Model" && t.Namespace?.StartsWith("VeSessionManager.Web.Pages") == true)
            ?? throw new InvalidOperationException(
                $"No page model found for a ParentCrumb pointing at './{pageName}' — did the page move or get renamed?");

        return type.GetCustomAttribute<AuthorizeAttribute>()?.Roles;
    }

    [Fact]
    public void EveryParentCrumbMirrorsItsTargetPagesAuthorizeRoles()
    {
        var pagesRoot = Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Web", "Pages");
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(pagesRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in CrumbPattern.Matches(text))
            {
                checkedCount++;
                var page = match.Groups["page"].Value;
                var crumbRoles = ResolveRoles(match.Groups["roles"].Value);
                var actualRoles = AuthorizeRolesOn(page);

                // Compared as sets: the ordering of a comma-separated role list is not meaningful,
                // and failing on it would be a false alarm someone "fixes" by reordering.
                var crumbSet = crumbRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
                var actualSet = (actualRoles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

                if (!crumbSet.SetEquals(actualSet))
                {
                    mismatches.Add(
                        $"{Path.GetFileName(file)} -> ./{page}: crumb says [{crumbRoles}], the page's [Authorize] says [{actualRoles ?? "(none)"}]");
                }
            }
        }

        Assert.True(checkedCount > 0,
            "No ParentCrumb usages were found — either they were all removed, or the pattern here no longer matches them and this test is now silently checking nothing.");

        Assert.True(mismatches.Count == 0,
            "A breadcrumb's roles must mirror the page it links to, or it offers a link to a 403 (too " +
            "permissive) or hides one someone was entitled to follow (too restrictive):\n  " +
            string.Join("\n  ", mismatches));
    }
}
