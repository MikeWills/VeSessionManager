using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #434: contention was handled in three places and named in none, so a worsening trend was
/// invisible until something else broke.
///
/// <para>These tests assert only the <b>wording</b> of what is logged. Every existing behaviour —
/// swallow the fault, run the job anyway, keep the host alive — is unchanged and is asserted by the
/// tests that already covered it. If any of those start failing, this change went too far.</para>
/// </summary>
public class ContentionLoggingTests
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex), ex));
    }

    /// <summary>
    /// A context whose saves fail the way the real one does under contention — EF's
    /// <c>DbUpdateException</c> wrapping a <c>SQLITE_BUSY</c>.
    /// </summary>
    private sealed class FailingContext(DbContextOptions<AppDbContext> options, Exception failure) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => Task.FromException<int>(failure);
    }

    private static DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static Exception Contention() => new DbUpdateException(
        "An error occurred while saving the entity changes. See the inner exception for details.",
        new SqliteException("SQLite Error 5: 'database is locked'.", 5));

    /// <summary>
    /// The start-row write losing a race for the file is the single most likely place this app meets
    /// contention — it happens on *every* job step, for every team, on every tick.
    /// </summary>
    [Fact]
    public async Task AContendedHistoryWrite_IsLoggedAsContention_AndTheJobStillRuns()
    {
        var logger = new RecordingLogger<JobRunHistoryLogger>();
        await using var dbContext = new FailingContext(Options(), Contention());
        var sut = new JobRunHistoryLogger(dbContext, logger);
        var jobRan = false;

        await sut.RunAsync("TestJob", _ => { jobRan = true; return Task.CompletedTask; }, null, CancellationToken.None);

        Assert.True(jobRan);  // unchanged: bookkeeping never costs us the actual work
        Assert.Contains(logger.Entries, e => e.Message.Contains("database contention", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The half that keeps the signal honest. If every failed bookkeeping write said "contention",
    /// the word would mean "a write failed" and counting it would answer nothing.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryFailedHistoryWrite_IsNotLabelledContention()
    {
        var logger = new RecordingLogger<JobRunHistoryLogger>();
        await using var dbContext = new FailingContext(Options(), new InvalidOperationException("something else"));
        var sut = new JobRunHistoryLogger(dbContext, logger);

        await sut.RunAsync("TestJob", _ => Task.CompletedTask, null, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("database contention", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The signal that moves *first*, and the one nothing could see before. The driver retries a
    /// busy database internally until the command timeout (30s by default), so a save that waited
    /// twelve seconds and then succeeded is indistinguishable from an instant one — no exception,
    /// no failure, nothing logged. Rising wait times are the early warning that #403's trigger #4
    /// is approaching; a failure is the late one.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(500, false)]
    [InlineData(2000, true)]
    [InlineData(30000, true)]
    public void ASlowWrite_IsWorthWarningAbout_OnlyPastTheThreshold(int milliseconds, bool expected)
        => Assert.Equal(expected, DatabaseContention.IsSlowWrite(TimeSpan.FromMilliseconds(milliseconds)));

    /// <summary>
    /// The threshold is a judgement, so pin it: local SQLite writes here are single-digit
    /// milliseconds, and anything second-scale means the write sat waiting for the other process.
    /// Low enough to see a trend forming, high enough that an ordinary tick never trips it.
    /// </summary>
    [Fact]
    public void TheSlowWriteThreshold_IsSecondScale_NotTunedToNoise()
        => Assert.Equal(TimeSpan.FromSeconds(1), DatabaseContention.SlowWriteThreshold);
}
