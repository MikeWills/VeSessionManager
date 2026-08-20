using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Answers one question: was this failure the two processes fighting over the one database file, or
/// a real error? (issue #434, follow-up to #403.)
///
/// <para><b>This is instrumentation, not control flow.</b> Nothing in this app retries, backs off,
/// or behaves differently based on the answer — it decides how a log line is worded, so that a
/// worsening contention trend can be seen before it becomes an outage. A wrong answer mislabels a
/// message; it never changes what the app does. That is what makes the SQLite-specific branch below
/// an acceptable cost rather than a coupling problem.</para>
///
/// <para><b>Why this exists at all.</b> Web and Worker are separate processes sharing one SQLite
/// file, so only one may write at a time. Contention is already *handled* in three places —
/// <c>JobTick.GuardedAsync</c>, <c>JobRunHistoryLogger</c>, and <c>PaymentGenerationService</c>'s
/// re-query — but until now it was *counted* in none of them, so the question "is SQLite still the
/// right call?" could only be answered from an argument. #403 decided it is, and listed rising lock
/// failures as the trigger to revisit. This is the thing that would show that rising.</para>
///
/// <para><b>⚠️ The portable check does not work on our provider, which was verified rather than
/// assumed.</b> <see cref="DbException.IsTransient"/> is documented as covering "failure to acquire
/// a database lock", explicitly so retry strategies can be written "without knowledge of specific
/// database error codes" — exactly the abstraction wanted here. But
/// <c>Microsoft.Data.Sqlite.SqliteException</c> <b>does not override it</b>: a genuine
/// <c>SQLITE_BUSY</c> inherits the base implementation and reports <c>false</c>. A classifier
/// written on <c>IsTransient</c> alone would compile, read correctly, survive review, and never
/// once fire on the only provider we run. <c>DatabaseContentionTests</c> pins that finding and will
/// fail if a future package version fixes it — at which point the SQLite branch here should be
/// deleted rather than left to rot.</para>
///
/// <para>So the order below is deliberate: <b>ask the portable question first</b>, so a swap to
/// MySQL/Postgres/SQL Server is classified correctly with no change here, and fall back to SQLite's
/// own codes only because that provider declines to answer it.</para>
/// </summary>
public static class DatabaseContention
{
    /// <summary>SQLite result code 5 — the database file is locked by another connection.</summary>
    private const int SqliteBusy = 5;

    /// <summary>SQLite result code 6 — a table in the database is locked.</summary>
    private const int SqliteLocked = 6;

    /// <summary>
    /// Wrapper chains deeper than this are a wrapper storm, not information — same posture and same
    /// reasoning as <c>JobRunHistoryLogger.Describe</c>'s cap, and it also makes a cyclic chain
    /// impossible to spin on.
    /// </summary>
    private const int MaxDepth = 5;

    /// <summary>
    /// Whether <paramref name="exception"/> — or anything it wraps — represents the database being
    /// locked by the other process.
    ///
    /// <para>The chain walk is not optional: EF Core never surfaces the driver's exception directly.
    /// Every save failure arrives as a <c>DbUpdateException</c> whose message is the famously
    /// unhelpful "See the inner exception for details", so an implementation that inspected only the
    /// outermost exception would answer "not contention" for every real case in this app.</para>
    /// </summary>
    public static bool IsContention(Exception? exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            // The portable question, asked first so a future provider needs no change here.
            if (current is DbException { IsTransient: true })
            {
                return true;
            }

            // The fallback our own provider forces. Deliberately narrow: every other SQLite code is
            // a real error, and a unique-index collision in particular is the *expected* outcome of
            // the known Web/Worker race that PaymentGenerationService distinguishes by re-querying.
            // Reporting that as contention would send someone hunting a lock problem that is not
            // happening — the exact misdiagnosis that comment exists to prevent.
            if (current is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    /// <summary>
    /// One word for a log message, so contention and ordinary failure are greppable apart without
    /// anyone having to parse exception text.
    /// </summary>
    public static string Describe(Exception? exception)
        => IsContention(exception) ? "database contention" : "error";

    /// <summary>
    /// How long a local write has to take before it is worth saying so.
    ///
    /// <para>Writes against a local SQLite file are single-digit milliseconds when uncontended, so a
    /// second-scale one did not do more work — it sat waiting for the other process to release the
    /// lock. Low enough that a trend is visible while it is still only a trend, high enough that an
    /// ordinary tick never trips it and nobody learns to ignore the line.</para>
    /// </summary>
    public static readonly TimeSpan SlowWriteThreshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether a write that <b>succeeded</b> took long enough to be evidence of contention.
    ///
    /// <para>⚠️ This is the signal that moves first, and the only one that existed nowhere before
    /// #434. Microsoft.Data.Sqlite retries a busy database internally until the command timeout —
    /// <a href="https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors">30 seconds
    /// by default</a>, and our connection strings set only <c>Data Source=</c>, so that default is
    /// in force. A save that waited twelve seconds and then succeeded raises no exception and logs
    /// nothing: it is indistinguishable from an instant one. By the time contention shows up as a
    /// *failure* it has already been getting worse for a long time, unobserved.</para>
    /// </summary>
    public static bool IsSlowWrite(TimeSpan elapsed) => elapsed >= SlowWriteThreshold;
}
