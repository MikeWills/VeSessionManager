using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>DatabaseContention.IsContention</c> — the one place that answers "was this failure the two
/// processes fighting over the one SQLite file, or a real error?" (issue #434, follow-up to #403).
///
/// <para>This classifier is <b>instrumentation, not control flow</b>. Nothing retries or behaves
/// differently on its answer; it only decides how a log line is worded. That is deliberate, and it
/// is what makes the SQLite-specific branch below an acceptable cost: a misclassification mislabels
/// a message, it never changes what the app does.</para>
/// </summary>
public class DatabaseContentionTests
{
    /// <summary>
    /// The portable path, and the reason this is not written against error codes alone.
    /// <c>DbException.IsTransient</c> is documented as covering "failure to acquire a database lock"
    /// so that "automatic retry execution strategies [can] be developed without knowledge of specific
    /// database error codes". Any provider that implements it is classified correctly with no
    /// change here — which is the whole point, since #403's answer to "should this be MySQL?" was
    /// "not yet, and keep the swap cheap".
    /// </summary>
    private sealed class TransientDbException(bool transient) : DbException("provider says transient")
    {
        public override bool IsTransient { get; } = transient;
    }

    [Fact]
    public void ATransientDbException_IsContention_WhateverTheProvider()
        => Assert.True(DatabaseContention.IsContention(new TransientDbException(true)));

    [Fact]
    public void ANonTransientDbException_IsNot()
        => Assert.False(DatabaseContention.IsContention(new TransientDbException(false)));

    /// <summary>
    /// ⚠️ The finding that shaped this class, verified against the real driver rather than assumed:
    /// <b>Microsoft.Data.Sqlite does not override <c>IsTransient</c></b>. A genuine
    /// <c>SQLITE_BUSY</c> — the actual "database is locked" this deployment hits — inherits
    /// <c>DbException</c>'s base implementation and reports <c>false</c>.
    ///
    /// <para>So a classifier written on <c>IsTransient</c> alone would compile, read correctly,
    /// pass a review, and never once fire on the only provider we run. This test exists to pin that,
    /// and to fail loudly if a future package version fixes it — at which point the SQLite branch
    /// in the implementation can be deleted rather than left to rot.</para>
    /// </summary>
    [Fact]
    public void SqliteStillDoesNotImplementIsTransient_WhichIsWhyTheFallbackExists()
    {
        var busy = new SqliteException("SQLite Error 5: 'database is locked'.", 5);

        Assert.False(busy.IsTransient);                          // the driver's answer — wrong for our purposes
        Assert.True(DatabaseContention.IsContention(busy));      // ours
    }

    [Theory]
    [InlineData(5)]  // SQLITE_BUSY
    [InlineData(6)]  // SQLITE_LOCKED
    public void SqliteBusyAndLocked_AreContention(int errorCode)
        => Assert.True(DatabaseContention.IsContention(new SqliteException("locked", errorCode)));

    /// <summary>
    /// The half that matters more. A unique-index collision is the *expected* outcome of the known
    /// Web/Worker race that <c>PaymentGenerationService</c> already distinguishes by re-querying —
    /// calling it contention would send someone hunting a lock problem that is not happening, which
    /// is the exact misdiagnosis that comment exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(1)]   // SQLITE_ERROR
    [InlineData(19)]  // SQLITE_CONSTRAINT
    public void OtherSqliteErrors_AreNot(int errorCode)
        => Assert.False(DatabaseContention.IsContention(new SqliteException("nope", errorCode)));

    /// <summary>
    /// EF Core never surfaces the driver's exception directly — every save failure arrives wrapped
    /// in <c>DbUpdateException</c>. A classifier that only inspected the outermost exception would
    /// therefore answer "not contention" for every real case in this app.
    /// </summary>
    [Fact]
    public void ContentionWrappedByEfCore_IsStillFound()
    {
        var wrapped = new DbUpdateException(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new SqliteException("SQLite Error 5: 'database is locked'.", 5));

        Assert.True(DatabaseContention.IsContention(wrapped));
    }

    [Fact]
    public void AnOrdinaryException_IsNot()
        => Assert.False(DatabaseContention.IsContention(new InvalidOperationException("boom")));

    [Fact]
    public void Null_IsNot()
        => Assert.False(DatabaseContention.IsContention(null));

    /// <summary>Guards against a cyclic or absurdly deep chain spinning the walk — same posture as <c>JobRunHistoryLogger.Describe</c>'s depth cap.</summary>
    [Fact]
    public void ADeeplyNestedChain_TerminatesRatherThanSpinning()
    {
        Exception ex = new SqliteException("locked", 5);
        for (var i = 0; i < 50; i++) ex = new InvalidOperationException("wrapper", ex);

        // Beyond the depth cap the answer is "not contention" — a wrapper storm is not information.
        Assert.False(DatabaseContention.IsContention(ex));
    }
}
