using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Keeps what was filed with ARRL, on disk (issue #197).
///
/// <para><b>Files rather than database columns</b>, decided against the numbers: an archive is ~377KB
/// and 279 sessions closed across this deployment's teams in the last year, so ~106MB/year into a
/// 4.5MB database — a 20x growth in year one, compounding, with every off-box backup re-shipping the
/// whole accumulated set. Two more reasons behind that one: deleting a BLOB row does not remove the
/// bytes from the SQLite file until a <c>VACUUM</c> (so a "purged" archive would linger on disk and
/// in every backup taken before it), and <c>VACUUM</c> is a poor fit here because Web and Worker
/// share one file and it rewrites under an exclusive lock.</para>
///
/// <para><b>The cost accepted in exchange: atomicity.</b> Row and file can diverge, so the purge
/// deletes the file before the row and reconciles orphans on a later pass.</para>
///
/// <para>⚠️ <b>The root must live outside the app directory</b> — <c>deploy.yml</c> runs
/// <c>rsync --delete</c> over it on every release, which is exactly why the database sits in
/// <c>/var/lib/vesessionmanager/</c>. And it needs adding to the off-box backup (#256), which covers
/// the database and key ring only; an unbacked-up archive fails silently, since nothing looks wrong
/// until a receipt is wanted and missing.</para>
/// </summary>
public class ArrlSubmissionArchiveStore(
    IOptions<ArrlSubmissionOptions> options,
    ILogger<ArrlSubmissionArchiveStore> logger)
{
    /// <summary>Anything outside this is replaced, so no third-party or operator-supplied string can shape a path.</summary>
    private static readonly Regex UnsafeSegment = new(@"[^A-Za-z0-9._-]", RegexOptions.Compiled);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ArchiveRootPath);

    /// <summary>
    /// <c>team/vec/year/month</c>, four deep — close to how these are filed by hand today, so the
    /// archive stays browsable by a person and not only by the app.
    ///
    /// <para>Built from codes rather than display names, and every segment is sanitized: a path
    /// assembled from values this app does not control is a traversal risk that no amount of "it
    /// looked fine" retires.</para>
    /// </summary>
    public static string BuildRelativeDirectory(string teamCode, string vecCode, DateTime sessionStartUtc) =>
        Path.Combine(
            Sanitize(teamCode),
            Sanitize(vecCode.ToLowerInvariant()),
            sessionStartUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            sessionStartUtc.ToString("MM", CultureInfo.InvariantCulture));

    /// <summary>
    /// Writes one file and returns its path relative to the root. The relative form is what the
    /// database stores, so moving the root does not orphan every archive at once.
    /// </summary>
    public async Task<string> SaveAsync(string relativeDirectory, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        if (options.Value.ArchiveRootPath is not { } root || string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "No ARRL archive directory is configured, so there is nowhere to keep what was filed. "
                + $"Set {ArrlSubmissionOptions.SectionName}:{nameof(ArrlSubmissionOptions.ArchiveRootPath)}.");
        }

        var safeName = Sanitize(fileName);
        var relativePath = Path.Combine(relativeDirectory, safeName);
        var fullPath = Path.Combine(root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);

        logger.LogInformation("Kept {ByteCount} bytes of ARRL submission evidence at {RelativePath}", content.Length, relativePath);
        return relativePath;
    }

    /// <summary>Absolute path for a stored relative one, for serving a download. Null when nothing is configured or the file is gone — a purged archive is an ordinary state, not an error.</summary>
    public string? ResolveFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || options.Value.ArchiveRootPath is not { } root
            || string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        // Belt and braces over the sanitizing above: a stored path that escapes the root is refused
        // outright rather than served, whatever put it there.
        if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Refusing an ARRL archive path that escapes the configured root: {RelativePath}", relativePath);
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>
    /// Collapses anything that is not a plain filename character. Applied to every segment and to the
    /// filename, so <c>..</c>, separators and control characters cannot survive into a path.
    /// </summary>
    private static string Sanitize(string value)
    {
        var cleaned = UnsafeSegment.Replace(value, "_").Trim('.', '_');
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
