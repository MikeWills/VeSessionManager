using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <see cref="UlsWatcherJob"/>'s anchored-slot guard and its settings precedence (issue #325).
///
/// <para><b>Why this job needs its own tests despite sharing a schedule with LicenseWatchJob.</b>
/// They share <see cref="DailySlotSchedule"/> and the same 08:00/20:00 ET anchor, and nothing else —
/// each has its own copy of the "has this slot already run?" query. #288 was exactly that: the two
/// copies diverged, one of them was wrong, and the shared schedule made it look as though testing
/// one covered the other. <see cref="LicenseWatchSlotGuardTests"/> is the sibling; this is the half
/// that was still untested.</para>
///
/// <para>Both halves matter because a wrong guard fails <i>silently in the safe-looking direction</i>:
/// the job returns early, writes no row, and the ops dashboard shows nothing at all — which is
/// indistinguishable from a quiet, healthy tick.</para>
/// </summary>
public class UlsWatcherSlotGuardTests
{
    /// <summary>
    /// 2026-08-11 18:00 UTC is 14:00 EDT. With the defaults (start 08:00 ET, every 12h) the slots are
    /// 08:00 and 20:00 ET, so the due slot is today's 08:00 ET — 12:00 UTC.
    /// </summary>
    private static readonly DateTime Now = new(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime InThisSlot = new(2026, 8, 11, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeThisSlot = new(2026, 8, 11, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>Records whether the tick got past the guard, without needing real ULS data.</summary>
    private sealed class CountingUlsClient : IUlsLookupClient
    {
        public int Calls { get; private set; }

        public Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<UlsLookupResult?>(UlsLookupResult.NotFound);
        }
    }

    private static async Task<WorkerTickHarness> CreateHarnessAsync(CountingUlsClient uls) =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IUlsLookupClient>(uls);
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<UlsWatcherService>();
        });

    private static UlsWatcherJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, harness.Configuration, new FixedTimeProvider(Now), Quiet.Logger<UlsWatcherJob>());

    private static async Task SeedHistoryAsync(WorkerTickHarness harness, bool success, DateTime startedUtc)
    {
        await using var dbContext = harness.NewContext();
        dbContext.JobRunHistories.Add(new JobRunHistory
        {
            JobName = JobSchedules.UlsWatcher,
            TeamId = null,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc,
            Success = success
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Counts only the rows this tick wrote, ignoring anything the test seeded.</summary>
    private static async Task<List<JobRunHistory>> RowsWrittenAsync(WorkerTickHarness harness)
    {
        await using var verify = harness.NewContext();
        return await verify.JobRunHistories.AsNoTracking()
            .Where(h => h.StartedUtc > InThisSlot).ToListAsync();
    }

    // ---- The guard ---------------------------------------------------------------------------

    [Fact]
    public async Task NoSuccessfulRunThisSlot_TheTickRuns()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Contains(await RowsWrittenAsync(harness), h => h.JobName == JobSchedules.UlsWatcher);
    }

    [Fact]
    public async Task ASuccessfulRunThisSlot_TheTickSkips()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await SeedHistoryAsync(harness, success: true, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Empty(await RowsWrittenAsync(harness));
        Assert.Equal(0, uls.Calls);
    }

    /// <summary>
    /// The point of anchoring: a slot with no <i>successful</i> run is retried on the next hourly
    /// tick rather than waiting for tomorrow. A guard that forgot <c>h.Success</c> would pass every
    /// other test here and fail this one.
    /// </summary>
    [Fact]
    public async Task AFailedRunThisSlot_TheTickRunsAgain()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await SeedHistoryAsync(harness, success: false, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.NotEmpty(await RowsWrittenAsync(harness));
    }

    /// <summary>
    /// Yesterday's success must not satisfy today's slot — otherwise the anchor fires once and never
    /// again, which is the failure the whole slot mechanism exists to prevent.
    /// </summary>
    [Fact]
    public async Task ASuccessFromBeforeTheDueSlot_DoesNotCount()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await SeedHistoryAsync(harness, success: true, BeforeThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.NotEmpty(await RowsWrittenAsync(harness));
    }

    /// <summary>
    /// The guard is scoped to this job's own name. <c>LicenseWatch</c> shares the anchor and runs in
    /// the same window, so a guard that matched on time alone would let a sibling's success suppress
    /// this job for the rest of the slot — the #288 shape, in the other direction.
    /// </summary>
    [Fact]
    public async Task AnotherJobsSuccessInThisSlot_DoesNotSatisfyThisGuard()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await using (var dbContext = harness.NewContext())
        {
            dbContext.JobRunHistories.Add(new JobRunHistory
            {
                JobName = JobSchedules.LicenseWatch,
                StartedUtc = InThisSlot,
                CompletedUtc = InThisSlot,
                Success = true
            });
            await dbContext.SaveChangesAsync();
        }

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Contains(await RowsWrittenAsync(harness), h => h.JobName == JobSchedules.UlsWatcher);
    }

    // ---- Settings precedence ------------------------------------------------------------------

    /// <summary>
    /// The admin-configurable schedule actually takes effect. With the start hour moved to 15:00 ET
    /// and a 12-hour interval, 14:00 ET is <i>before</i> the day's first slot, so the due slot is
    /// yesterday's 15:00 ET — and a success seeded at 13:00 UTC today already covers it.
    ///
    /// <para>Without this, a settings read that silently returned the defaults would be invisible:
    /// every other test here uses the defaults and would pass unchanged.</para>
    /// </summary>
    [Fact]
    public async Task TheSlotComesFromSystemSettings_NotOnlyTheDefaults()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await using (var dbContext = harness.NewContext())
        {
            dbContext.SystemSettings.Add(new SystemSettings
            {
                Id = SystemSettingsService.SingletonId,
                UlsWatcherStartHourEt = 15,
                UlsWatcherIntervalHours = 12
            });
            await dbContext.SaveChangesAsync();
        }

        await SeedHistoryAsync(harness, success: true, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        // Under the *defaults* the due slot would be 12:00 UTC today, which the 13:00 success also
        // covers — so this case is only meaningful together with the one below.
        Assert.Empty(await RowsWrittenAsync(harness));
        Assert.Equal(0, uls.Calls);
    }

    /// <summary>
    /// The discriminating half of the pair above. Same seeded success, but the configured start hour
    /// puts the due slot <i>after</i> it, so the tick must run. A settings read that returned the
    /// defaults skips here, because under the defaults the 13:00 success covers the 12:00 slot.
    /// </summary>
    [Fact]
    public async Task ASuccessBeforeTheConfiguredSlot_DoesNotSuppressTheTick()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await using (var dbContext = harness.NewContext())
        {
            // 13:00 ET today = 17:00 UTC, which is after the 13:00 UTC success below.
            dbContext.SystemSettings.Add(new SystemSettings
            {
                Id = SystemSettingsService.SingletonId,
                UlsWatcherStartHourEt = 13,
                UlsWatcherIntervalHours = 24
            });
            await dbContext.SaveChangesAsync();
        }

        await SeedHistoryAsync(harness, success: true, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.NotEmpty(await RowsWrittenAsync(harness));
    }

    /// <summary>
    /// Non-vacuity: the seed helper and the "rows written" probe must be able to disagree, or the
    /// skip cases could pass by looking at nothing.
    /// </summary>
    [Fact]
    public async Task TheRowsWrittenProbe_SeesARowWhenThereIsOne()
    {
        await using var harness = await CreateHarnessAsync(new CountingUlsClient());

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Single(await RowsWrittenAsync(harness));
    }
}
