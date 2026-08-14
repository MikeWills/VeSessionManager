using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Retention;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <see cref="RecordRetentionJob"/> — retention for AuditLogs (#86) and JobRunHistories (#296).
///
/// <para>Real SQLite rather than EF InMemory is not a preference here, it is a requirement:
/// <c>ExecuteDeleteAsync</c> is the whole mechanism and InMemory does not support it at all.</para>
///
/// <para>The case that matters most is the <b>off</b> one. Both windows ship null, so on every
/// existing deployment this job's correct behaviour is to delete nothing at all — a bug that made
/// an unset window mean "zero days" would silently erase every audit entry the first night it ran.</para>
/// </summary>
public class RecordRetentionJobTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<WorkerTickHarness> CreateHarnessAsync() =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<SystemSettingsService>();
            services.AddScoped<RecordRetentionService>();
        });

    /// <summary>Seeds the settings row with the two windows under test; null means "keep forever".</summary>
    private static async Task SeedSettingsAsync(WorkerTickHarness harness, int? auditDays, int? jobDays)
    {
        await using var dbContext = harness.NewContext();
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = 1,
            AuditLogRetentionDays = auditDays,
            JobRunHistoryRetentionDays = jobDays
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedRowsAsync(WorkerTickHarness harness)
    {
        await using var dbContext = harness.NewContext();

        // One comfortably inside any window under test, one comfortably outside it.
        dbContext.AuditLogs.Add(new AuditLog { Action = "Recent", EntityType = "Test", EntityId = 1, TimestampUtc = Now.AddDays(-5) });
        dbContext.AuditLogs.Add(new AuditLog { Action = "Ancient", EntityType = "Test", EntityId = 2, TimestampUtc = Now.AddDays(-400) });

        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "Recent", StartedUtc = Now.AddDays(-5), Success = true });
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "Ancient", StartedUtc = Now.AddDays(-400), Success = true });

        await dbContext.SaveChangesAsync();
    }

    private static RecordRetentionJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, harness.Configuration, Quiet.Logger<RecordRetentionJob>());

    /// <summary>
    /// The default state of every deployment: both windows unset. Nothing is deleted, and the job
    /// still records a successful run so the ops page shows it working rather than absent.
    /// </summary>
    [Fact]
    public async Task NeitherWindowConfigured_DeletesNothing()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedSettingsAsync(harness, auditDays: null, jobDays: null);
        await SeedRowsAsync(harness);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        Assert.Equal(2, await verify.AuditLogs.CountAsync());

        // Two seeded, plus this run's own row.
        Assert.Equal(3, await verify.JobRunHistories.CountAsync());
    }

    /// <summary>Each window governs only its own table — turning one on must not prune the other.</summary>
    [Fact]
    public async Task AuditWindowOnly_PrunesAuditRowsAndLeavesJobHistoryAlone()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedSettingsAsync(harness, auditDays: 90, jobDays: null);
        await SeedRowsAsync(harness);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var audits = await verify.AuditLogs.AsNoTracking().ToListAsync();
        Assert.Equal("Recent", Assert.Single(audits).Action);
        Assert.Equal(3, await verify.JobRunHistories.CountAsync());
    }

    [Fact]
    public async Task JobHistoryWindowOnly_PrunesJobRunsAndLeavesAuditAlone()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedSettingsAsync(harness, auditDays: null, jobDays: 90);
        await SeedRowsAsync(harness);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        Assert.Equal(2, await verify.AuditLogs.CountAsync());

        var runs = await verify.JobRunHistories.AsNoTracking().Select(h => h.JobName).ToListAsync();
        Assert.DoesNotContain("Ancient", runs);
        Assert.Contains("Recent", runs);
    }

    /// <summary>
    /// The job records its own run, and that row must survive its own sweep. It is written after the
    /// delete and stamped now, so no window can reach it — asserted rather than assumed, because a
    /// job that pruned its own history would report nothing had ever run.
    /// </summary>
    [Fact]
    public async Task ItsOwnRunIsRecordedAndSurvivesTheSweep()
    {
        await using var harness = await CreateHarnessAsync();
        await SeedSettingsAsync(harness, auditDays: 1, jobDays: 1);
        await SeedRowsAsync(harness);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var own = await verify.JobRunHistories.AsNoTracking()
            .SingleAsync(h => h.JobName == JobSchedules.RecordRetention);
        Assert.True(own.Success);

        // Everything seeded was older than one day, so only this job's own row is left.
        Assert.Equal(1, await verify.JobRunHistories.CountAsync());
        Assert.Equal(0, await verify.AuditLogs.CountAsync());
    }
}
