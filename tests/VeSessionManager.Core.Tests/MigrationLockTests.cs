using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #443 — Web and Worker both call <c>Database.Migrate()</c> at startup and nothing in the code
/// stopped them doing it at once.
///
/// <para>The only protection was <c>deploy.yml</c> starting Worker, asserting it active, then Web.
/// That is workflow sequencing: it holds for a deploy on the one box attached to the pipeline, and
/// not for a reboot (systemd starts both units together), not for the HRCC server, not for any
/// self-hoster following the documented <c>dotnet publish</c> install, and not for a crash-restart
/// (both units are <c>Restart=always</c>).</para>
///
/// <para>The failure is not clean either: a transient "database is locked" thrown here escapes
/// <b>outside</b> <c>JobTick.GuardedAsync</c>, and .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> means the whole Worker stops.</para>
/// </summary>
public class MigrationLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vsm-migration-lock-{Guid.NewGuid():N}");

    public MigrationLockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string ConnectionString() => $"Data Source={Path.Combine(_directory, "vesessionmanager.db")}";

    /// <summary>The ordinary case: nothing else holds it, so the work runs.</summary>
    [Fact]
    public void TheWorkRuns_WhenNothingElseHoldsTheLock()
    {
        var ran = false;

        MigrationLock.Run(ConnectionString(), NullProgress, () => ran = true);

        Assert.True(ran);
    }

    /// <summary>
    /// The point of the whole thing. While one host holds the lock, a second cannot enter — so the
    /// two <c>Migrate()</c> calls cannot overlap however systemd happens to start the units.
    /// </summary>
    [Fact]
    public void ASecondHost_CannotEnterWhileTheFirstHoldsIt()
    {
        var connectionString = ConnectionString();
        var secondRan = false;

        MigrationLock.Run(connectionString, NullProgress, () =>
        {
            // Reentering from inside the callback is the same shape as the other process trying while
            // this one migrates — a short wait so the test cannot hang if the lock is not honoured.
            Assert.Throws<TimeoutException>(() =>
                MigrationLock.Run(connectionString, NullProgress, () => secondRan = true, TimeSpan.FromMilliseconds(300)));
        });

        Assert.False(secondRan);
    }

    /// <summary>And once released, the next host gets in — a lock that never frees is an outage of its own.</summary>
    [Fact]
    public void TheLockIsReleased_SoTheNextHostProceeds()
    {
        var connectionString = ConnectionString();
        MigrationLock.Run(connectionString, NullProgress, () => { });

        var secondRan = false;
        MigrationLock.Run(connectionString, NullProgress, () => secondRan = true);

        Assert.True(secondRan);
    }

    /// <summary>Released even when the work throws, or one failed migration would wedge every future start.</summary>
    [Fact]
    public void TheLockIsReleased_EvenWhenTheWorkThrows()
    {
        var connectionString = ConnectionString();

        Assert.Throws<InvalidOperationException>(() =>
            MigrationLock.Run(connectionString, NullProgress, () => throw new InvalidOperationException("migration blew up")));

        var secondRan = false;
        MigrationLock.Run(connectionString, NullProgress, () => secondRan = true);

        Assert.True(secondRan);
    }

    /// <summary>
    /// Waiting is announced. A host silently blocked on a lock is indistinguishable from a hung one,
    /// which is exactly the sort of thing that gets diagnosed as a broken deployment.
    /// </summary>
    [Fact]
    public void WaitingIsReported_SoABlockedHostDoesNotLookHung()
    {
        var connectionString = ConnectionString();
        var reported = new List<string>();

        MigrationLock.Run(connectionString, NullProgress, () =>
        {
            try
            {
                // Longer than MigrationLock's quiet period, or the wait ends before it has anything
                // to say — the point is that a *sustained* wait is announced, not every momentary one.
                MigrationLock.Run(connectionString, reported.Add, () => { }, TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // expected — the assertion is about what was said while waiting
            }
        });

        Assert.NotEmpty(reported);
    }

    /// <summary>
    /// ⚠️ In-memory and shared-cache SQLite have no file to lock, and the whole test suite uses them.
    /// A lock that tried to take one anyway would either fail or serialize every test in the process.
    /// </summary>
    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("DataSource=:memory:")]
    [InlineData("Data Source=InMemorySample;Mode=Memory;Cache=Shared")]
    public void AMemoryDatabase_NeedsNoLock_AndStillRunsTheWork(string connectionString)
    {
        var ran = false;

        MigrationLock.Run(connectionString, NullProgress, () => ran = true);

        Assert.True(ran);
    }

    /// <summary>
    /// The lock file sits beside the database rather than in the app directory — the service account
    /// writes the data directory and deliberately does not write <c>/opt/vesessionmanager</c>, and a
    /// lock in a tree that <c>rsync --delete</c> replaces mid-deploy would be worse than none.
    ///
    /// <para>Asserted from inside the callback because the handle is opened <c>DeleteOnClose</c>: the
    /// file exists only while held, which is also what stops a crashed host leaving one behind.</para>
    /// </summary>
    [Fact]
    public void TheLockFile_SitsBesideTheDatabase_WhileItIsHeld()
    {
        string[] whileHeld = [];

        MigrationLock.Run(ConnectionString(), NullProgress,
            () => whileHeld = Directory.GetFiles(_directory, "*.migration-lock"));

        Assert.NotEmpty(whileHeld);
        Assert.Empty(Directory.GetFiles(_directory, "*.migration-lock"));  // and cleaned up after
    }

    /// <summary>An unusable path must not stop the app starting — migrating is what matters, the lock is a guard around it.</summary>
    [Fact]
    public void AnUnusableLockPath_DoesNotStopTheMigration()
    {
        var ran = false;

        MigrationLock.Run($"Data Source={Path.Combine(_directory, "no-such-dir", "x.db")}", NullProgress, () => ran = true);

        Assert.True(ran);
    }

    private static void NullProgress(string message) { }
}
