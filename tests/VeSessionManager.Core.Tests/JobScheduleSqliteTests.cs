using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The Job Schedule page's grouped last-run query, pinned against real SQLite.
///
/// <para><b>Why this is not covered by <see cref="JobScheduleServiceTests"/>.</b> That query is a
/// <c>GroupBy</c> with two aggregates, one of them filtered — a shape this codebase has been bitten by
/// twice. InMemory evaluates it as plain LINQ, so it can pass a query that will not translate; and the
/// nullable-cast bug it did catch (<c>Max</c> over an empty filtered sequence) proves the aggregate is
/// where the sharp edges are. Same reasoning as <see cref="ActiveCandidateCountSqliteTests"/>.</para>
/// </summary>
public class JobScheduleSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 14, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(); // an in-memory DB lives only as long as its connection
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    private static JobScheduleService CreateService(AppDbContext dbContext) =>
        new(dbContext, new ConfigurationBuilder().AddInMemoryCollection([]).Build(), new FixedTimeProvider(Now));

    private static void SeedRun(AppDbContext dbContext, string jobName, DateTime startedUtc, bool success) =>
        dbContext.JobRunHistories.Add(new JobRunHistory
        {
            JobName = jobName,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc.AddMinutes(1),
            Success = success
        });

    /// <summary>
    /// The full page query against SQL, with the three cases that shape the aggregate: a job with
    /// mixed outcomes, a job that has only ever failed (the empty filtered sequence), and a job with
    /// no history at all (absent from the group join entirely).
    /// </summary>
    [Fact]
    public async Task LastRunQuery_TranslatesToSql_AcrossMixedFailedAndAbsentHistories()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        // Mixed: the successful run is older than the failed one, so "last run" and "last success"
        // must come back as different values rather than one standing in for the other.
        SeedRun(dbContext, JobSchedules.PaymentReminder, Now.AddHours(-30), success: true);
        SeedRun(dbContext, JobSchedules.PaymentReminder, Now.AddHours(-2), success: false);

        // Only-ever-failed: this is the row that threw before the nullable cast moved inside Max.
        SeedRun(dbContext, JobSchedules.PiiPurge, Now.AddHours(-3), success: false);

        // SessionIngestion deliberately has no rows at all.
        await dbContext.SaveChangesAsync();

        var statuses = await CreateService(dbContext).GetStatusesAsync(CancellationToken.None);

        var reminder = statuses.Single(s => s.Descriptor.JobName == JobSchedules.PaymentReminder);
        Assert.Equal(Now.AddHours(-2), reminder.LastRunUtc);
        Assert.Equal(Now.AddHours(-30), reminder.LastSuccessUtc);
        Assert.False(reminder.LastRunSucceeded);

        var purge = statuses.Single(s => s.Descriptor.JobName == JobSchedules.PiiPurge);
        Assert.Equal(Now.AddHours(-3), purge.LastRunUtc);
        Assert.Null(purge.LastSuccessUtc);
        Assert.False(purge.LastRunSucceeded);

        var ingestion = statuses.Single(s => s.Descriptor.JobName == JobSchedules.SessionIngestion);
        Assert.Null(ingestion.LastRunUtc);
        Assert.Equal(NextRunConfidence.Unknown, ingestion.Confidence);

        Assert.Equal(JobSchedules.All.Count, statuses.Count);
    }

    /// <summary>
    /// A "Manual"-prefixed run must not be picked up by the name match. Pinned against SQL because
    /// the filter is a <c>Contains</c> over a list, which translates to <c>IN</c> — exact-match
    /// semantics, but worth proving rather than assuming given what rides on it.
    /// </summary>
    [Fact]
    public async Task ManualRuns_AreExcludedBySql_NotJustInMemory()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        SeedRun(dbContext, "Manual" + JobSchedules.SessionIngestion, Now.AddMinutes(-1), success: true);
        await dbContext.SaveChangesAsync();

        var statuses = await CreateService(dbContext).GetStatusesAsync(CancellationToken.None);

        var ingestion = statuses.Single(s => s.Descriptor.JobName == JobSchedules.SessionIngestion);
        Assert.Null(ingestion.LastRunUtc);
    }
}
