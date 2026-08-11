using Microsoft.Extensions.Logging;
using VeSessionManager.Worker;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <c>JobTick.GuardedAsync</c> — the most load-bearing behaviour in the Worker, and until now
/// asserted nowhere (issue #325).
///
/// <para>.NET's default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so anything
/// escaping an <c>ExecuteAsync</c> takes down the <b>entire Worker process</b> — every job, not just
/// the one that threw. Web and Worker share one SQLite file, so a transient "database is locked"
/// during any per-tick query is enough to trigger it. That is not hypothetical: the 2026-07-21
/// incident where an unconfigured Square credential threw from a constructor killed
/// ExamTools/Zoom/Discord polling too.</para>
///
/// <para>So the contract is exactly two rules, and both matter in opposite directions:
/// <b>swallow faults</b> (or one bad row ends the process) and <b>propagate cancellation</b> (or
/// shutdown hangs). A test that only checked the first would happily pass an implementation that
/// bricks graceful shutdown.</para>
/// </summary>
public class JobTickTests
{
    /// <summary>Records what was logged, so "it was swallowed" can be distinguished from "it vanished".</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task ASuccessfulTick_RunsTheBody_AndLogsNothing()
    {
        var logger = new RecordingLogger();
        var ran = false;

        await JobTick.GuardedAsync(logger, "TestJob", () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// The rule the Worker's survival rests on. Without it this exception reaches the host and stops
    /// every other job in the process.
    /// </summary>
    [Fact]
    public async Task AThrowingTick_IsSwallowed_SoTheHostSurvives()
    {
        var logger = new RecordingLogger();
        var boom = new InvalidOperationException("database is locked");

        // The assertion is the absence of a throw: this line completing IS the behaviour.
        await JobTick.GuardedAsync(logger, "TestJob", () => throw boom);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);          // the original, not a wrapper
        Assert.Contains("TestJob", entry.Message);   // says which job, or the log is unusable
    }

    [Fact]
    public async Task AThrowingTick_DoesNotPreventTheNextTick()
    {
        var logger = new RecordingLogger();
        var secondRan = false;

        await JobTick.GuardedAsync(logger, "TestJob", () => throw new InvalidOperationException("boom"));
        await JobTick.GuardedAsync(logger, "TestJob", () =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(secondRan);
    }

    /// <summary>
    /// The opposite rule, and the reason a "catch everything" implementation would be wrong:
    /// cancellation is shutdown, not a fault. Swallowing it leaves the <c>do/while</c> in every job
    /// spinning until the host's shutdown timeout expires and systemd SIGKILLs the process —
    /// potentially mid-<c>SaveChangesAsync</c>.
    /// </summary>
    [Fact]
    public async Task ACancelledTick_Propagates_SoShutdownIsNotSwallowed()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JobTick.GuardedAsync(logger, "TestJob", () =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }));

        // Not logged as an error: a clean shutdown is not a fault, and logging it as one trains
        // people to ignore the Error level on this job.
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// <c>TaskCanceledException</c> derives from <c>OperationCanceledException</c>, so it takes the
    /// propagate path too. Worth pinning explicitly: it is what an actual awaited-and-cancelled EF
    /// call throws, which is the realistic shutdown shape rather than the hand-thrown one above.
    /// </summary>
    [Fact]
    public async Task ACancelledAwait_AlsoPropagates()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JobTick.GuardedAsync(logger, "TestJob", async () => await Task.Delay(Timeout.Infinite, cts.Token)));

        Assert.Empty(logger.Entries);
    }
}
