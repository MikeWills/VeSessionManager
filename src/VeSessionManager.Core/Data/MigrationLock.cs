using Microsoft.Data.Sqlite;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Serializes startup migrations across the two hosts (issue #443).
///
/// <para><b>What was wrong.</b> Web and Worker both call <c>Database.Migrate()</c> at startup, and
/// nothing in the code stopped them doing it simultaneously. The only protection was
/// <c>deploy.yml</c> starting Worker, asserting it active, then Web — workflow sequencing, which
/// holds for a deploy on the one box attached to the pipeline and nowhere else. Not for a reboot
/// (systemd starts both units together, with no <c>After=</c> between them), not for the HRCC server
/// which is attached to no pipeline, not for a self-hoster following the documented
/// <c>dotnet publish</c> install, and not for a crash-restart, since both units are
/// <c>Restart=always</c>.</para>
///
/// <para><b>Why it is not a small race.</b> A transient "database is locked" thrown from the migrate
/// call escapes <i>outside</i> <c>JobTick.GuardedAsync</c>, and .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> stops the entire Worker — every job, until
/// somebody notices and restarts it.</para>
///
/// <para><b>Why a lock file rather than something cleverer.</b> SQLite has no advisory lock to
/// borrow, and a named <c>Mutex</c> is process-local on Unix rather than machine-wide. The two hosts
/// are always on one machine sharing one file, so an exclusive file lock is exactly the right scope.
/// It sits <b>beside the database</b>, not in the app directory: the service account writes the data
/// directory and deliberately cannot write <c>/opt/vesessionmanager</c>, and a lock inside a tree
/// that <c>rsync --delete</c> replaces mid-deploy would be worse than no lock at all.</para>
///
/// <para><c>deploy.yml</c>'s ordering can stay as belt-and-braces, but it stops being load-bearing.</para>
/// </summary>
public static class MigrationLock
{
    /// <summary>
    /// How long a host waits for the other to finish migrating before giving up.
    ///
    /// <para>Generous, because the cost of the two errors is wildly asymmetric: waiting too long
    /// delays one startup, while giving up too early reintroduces the exact concurrent migration this
    /// exists to prevent. A real migration on this database is seconds; a minute is already far past
    /// anything legitimate.</para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to wait before saying anything. Below this a wait is normal and silence is correct.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs <paramref name="migrate"/> with the machine-wide migration lock held.
    /// </summary>
    /// <param name="connectionString">The app's connection string — the lock file is derived from its <c>Data Source</c>.</param>
    /// <param name="report">Called when the wait becomes long enough to be worth announcing. A host blocked in silence is indistinguishable from a hung one.</param>
    /// <param name="migrate">The migration work. Runs exactly once, whether this host took the lock or waited for it.</param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>. Tests use a short one; nothing in the app should.</param>
    /// <exception cref="TimeoutException">The other host held the lock for longer than <paramref name="timeout"/>.</exception>
    public static void Run(string connectionString, Action<string> report, Action migrate, TimeSpan? timeout = null)
    {
        var lockPath = ResolveLockPath(connectionString);
        if (lockPath is null)
        {
            // No file-backed database — an in-memory or shared-cache one, which is every test in the
            // suite. There is nothing for a second process to contend over, and taking a lock anyway
            // would serialize the whole test run.
            migrate();
            return;
        }

        FileStream? handle;
        try
        {
            handle = Acquire(lockPath, report, timeout ?? DefaultTimeout);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            // The lock is a guard around the migration, not the point of it. If the path cannot be
            // used at all — directory missing, permissions, a filesystem that will not lock — the
            // right outcome is still to migrate. Refusing to start over an unavailable guard would
            // turn a hardening measure into an outage.
            migrate();
            return;
        }

        try
        {
            migrate();
        }
        finally
        {
            // Released even when the migration throws: otherwise one failed migration wedges every
            // future start of both hosts, which is a far worse failure than the one this prevents.
            handle.Dispose();
        }
    }

    private static FileStream Acquire(string lockPath, Action<string> report, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;
        var startedWaiting = DateTime.UtcNow;

        while (true)
        {
            try
            {
                // FileShare.None is the whole mechanism: the second host's open fails until the first
                // disposes. DeleteOnClose keeps the data directory tidy without a separate cleanup.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            // ⚠️ DirectoryNotFoundException and FileNotFoundException DERIVE from IOException, so a
            // bare `catch (IOException)` treats an unusable path as contention and spins here for the
            // whole timeout. That is a startup hang, which is worse than the race being prevented —
            // caught by AnUnusableLockPath_DoesNotStopTheMigration before it shipped. Only a genuine
            // sharing violation retries; everything else propagates to the fallback in Run.
            catch (IOException ex) when (ex is not DirectoryNotFoundException and not FileNotFoundException)
            {
                // Held by the other host.
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Timed out after {timeout.TotalSeconds:F0}s waiting for the other host to finish applying database migrations (lock file {lockPath}). " +
                        "Both VeSessionManager.Web and VeSessionManager.Worker migrate at startup and take this lock; if neither is running, delete the lock file.");
                }

                if (!announced && DateTime.UtcNow - startedWaiting > QuietPeriod)
                {
                    announced = true;
                    report("Waiting for the other host to finish applying database migrations before starting.");
                }

                Thread.Sleep(PollInterval);
            }
        }
    }

    /// <summary>
    /// The lock file for a connection string, or null when there is no file to lock.
    ///
    /// <para>⚠️ Returning null for an in-memory database is load-bearing, not defensive: EF InMemory
    /// and <c>DataSource=:memory:</c> SQLite are what the entire test suite runs on, and
    /// <c>Mode=Memory</c> is how the shared-cache form is spelled.</para>
    /// </summary>
    private static string? ResolveLockPath(string connectionString)
    {
        string? dataSource;
        try
        {
            dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (Exception)
        {
            // Not a SQLite connection string at all — a different provider, or malformed. Either way
            // this is not ours to lock.
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(dataSource) + ".migration-lock";
    }
}
