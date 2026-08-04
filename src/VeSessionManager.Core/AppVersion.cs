using System.Reflection;

namespace VeSessionManager.Core;

/// <summary>
/// The build's version as shown in the UI footer.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// A released build is stamped by CI with the pushed git tag (<c>v1.2.0</c>) as its whole
    /// informational version, so a leading "v" is what distinguishes it from a local/untagged
    /// build carrying the SDK's default 1.0.0.
    /// </summary>
    public static string Display { get; } = Build();

    private static string Build()
    {
        var informational = (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

        // The SDK appends "+{commit sha}" to whatever InformationalVersion was set.
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        var commit = plus >= 0 ? informational[(plus + 1)..] : string.Empty;

        if (version.StartsWith('v'))
        {
            return version;
        }

        return commit.Length >= 7 ? $"pre-release ({commit[..7]})" : "pre-release";
    }
}
