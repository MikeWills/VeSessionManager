using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Keeping what was filed with ARRL on disk (issue #197).
///
/// <para>The layout is <c>team/vec/year/month</c>, close to how these are filed by hand today so the
/// archive stays browsable by a person. The interesting part is not the layout but the sanitizing:
/// the segments come from stored codes and the filename from ExamTools or an operator's upload, and a
/// path assembled from values this app does not control is a traversal risk.</para>
/// </summary>
public class ArrlSubmissionArchiveStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vesm-arrl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ArrlSubmissionArchiveStore Store(string? rootOverride = null) =>
        new(Options.Create(new ArrlSubmissionOptions { ArchiveRootPath = rootOverride ?? root }),
            NullLogger<ArrlSubmissionArchiveStore>.Instance);

    private static readonly DateTime SessionStartUtc = new(2026, 4, 22, 1, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TheLayoutIsTeamThenVecThenYearThenMonth()
    {
        var directory = ArrlSubmissionArchiveStore.BuildRelativeDirectory("MARC", "arrl", SessionStartUtc);

        Assert.Equal(Path.Combine("MARC", "arrl", "2026", "04"), directory);
    }

    [Fact]
    public void TheVecSegmentIsLowercased()
    {
        var directory = ArrlSubmissionArchiveStore.BuildRelativeDirectory("MARC", "ARRL", SessionStartUtc);

        Assert.Contains(Path.Combine("MARC", "arrl"), directory);
    }

    /// <summary>A team code is stored data, not a promise — nothing stops one containing a separator.</summary>
    [Fact]
    public void ASegmentCannotEscapeWithPathCharacters()
    {
        var directory = ArrlSubmissionArchiveStore.BuildRelativeDirectory("../../etc", "arrl", SessionStartUtc);

        Assert.DoesNotContain("..", directory);
        Assert.StartsWith("etc", directory);
    }

    [Fact]
    public async Task AFileIsWrittenUnderTheRootAndItsRelativePathReturned()
    {
        var directory = ArrlSubmissionArchiveStore.BuildRelativeDirectory("MARC", "arrl", SessionStartUtc);

        var relativePath = await Store().SaveAsync(directory, "ExamSession_MARC_20260422_0130_arrl.zip", [1, 2, 3, 4], CancellationToken.None);

        Assert.Equal(Path.Combine("MARC", "arrl", "2026", "04", "ExamSession_MARC_20260422_0130_arrl.zip"), relativePath);
        Assert.True(File.Exists(Path.Combine(root, relativePath)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(root, relativePath)));
    }

    /// <summary>
    /// The relative path is what the database stores, so moving the root relocates every archive at
    /// once instead of orphaning them.
    /// </summary>
    [Fact]
    public async Task TheStoredPathIsRelativeToTheRoot()
    {
        var relativePath = await Store().SaveAsync("MARC", "a.zip", [1], CancellationToken.None);

        Assert.False(Path.IsPathRooted(relativePath));
    }

    /// <summary>
    /// An uploaded filename is operator-supplied and reaches this method directly. It must not be able
    /// to write outside the tree, whatever it says.
    /// </summary>
    [Fact]
    public async Task AFilenameCannotEscapeTheDirectory()
    {
        var relativePath = await Store().SaveAsync("MARC", "../../../evil.pdf", [1], CancellationToken.None);

        Assert.DoesNotContain("..", relativePath);
        var written = Path.GetFullPath(Path.Combine(root, relativePath));
        Assert.StartsWith(Path.GetFullPath(root), written);
    }

    [Fact]
    public async Task AnUnconfiguredRoot_RefusesLoudlyRatherThanDroppingTheEvidence()
    {
        var store = Store(rootOverride: "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync("MARC", "a.zip", [1], CancellationToken.None));
    }

    [Fact]
    public async Task AStoredFileResolvesBackToAnAbsolutePath()
    {
        var relativePath = await Store().SaveAsync("MARC", "a.zip", [1], CancellationToken.None);

        Assert.NotNull(Store().ResolveFullPath(relativePath));
    }

    /// <summary>A purged archive is an ordinary state once retention is switched on, not an error to throw over.</summary>
    [Fact]
    public void AMissingFileResolvesToNull()
    {
        Assert.Null(Store().ResolveFullPath(Path.Combine("MARC", "gone.zip")));
    }

    /// <summary>
    /// Belt and braces over the sanitizing: a stored path that escapes the root is refused rather than
    /// served, whatever wrote it. The download route reads this value, so it is the last line before
    /// arbitrary file disclosure.
    /// </summary>
    [Fact]
    public void APathThatEscapesTheRootIsRefused()
    {
        Assert.Null(Store().ResolveFullPath(Path.Combine("..", "..", "windows", "win.ini")));
    }

    [Fact]
    public void NoRootConfigured_ResolvesToNullRatherThanThrowing()
    {
        Assert.Null(Store(rootOverride: "").ResolveFullPath("MARC/a.zip"));
    }
}
