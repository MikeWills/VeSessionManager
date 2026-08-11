using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// Issue #288: <c>LicenseWatchJob</c> writes <b>two</b> independent <c>JobRunHistory</c> rows per
/// tick — <c>LicenseWatch</c> and <c>VeLicenseWatch</c> — but its anchored-slot guard checked only
/// the first name.
///
/// <para>So if <c>LicenseWatch</c> succeeded and <c>VeLicenseWatch</c> threw (an FRN collision, say),
/// the next hourly tick saw a successful <c>LicenseWatch</c> since the slot and returned early. VE
/// licence refresh then <b>never retried for the rest of the day</b> — one green row beside one red
/// one, with no retry — while the code comment beside it claimed separate rows were precisely what
/// prevented that.</para>
///
/// <para>The fix was shipped without a test. Given that #232 from the same audit turned out not to
/// reproduce at all, an unverified fix in the same family is not something to leave sitting: the
/// discriminating test below was run against the pre-fix single-name guard, and fails there.</para>
/// </summary>
public class LicenseWatchSlotGuardTests
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
            services.AddScoped<SystemSettingsService>();
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<LicenseWatchService>();
            services.AddScoped<VolunteerExaminerLicenseWatchService>();
        });

    private static async Task SeedHistoryAsync(WorkerTickHarness harness, string jobName, bool success, DateTime startedUtc)
    {
        await using var dbContext = harness.NewContext();
        dbContext.JobRunHistories.Add(new JobRunHistory
        {
            JobName = jobName,
            TeamId = null,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc,
            Success = success
        });
        await dbContext.SaveChangesAsync();
    }

    private static LicenseWatchJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, new FixedTimeProvider(Now), Quiet.Logger<LicenseWatchJob>());

    /// <summary>Counts only the rows this tick wrote, ignoring anything the test seeded.</summary>
    private static async Task<List<JobRunHistory>> RowsWrittenAsync(WorkerTickHarness harness)
    {
        await using var verify = harness.NewContext();
        return await verify.JobRunHistories
            .AsNoTracking()
            .Where(h => h.StartedUtc > InThisSlot)
            .ToListAsync();
    }

    // ---- The regression ---------------------------------------------------------------------

    /// <summary>
    /// **The discriminating case.** One half succeeded this slot and the other did not, so the tick
    /// must still run. Against the pre-fix guard — which asked only about <c>LicenseWatch</c> — this
    /// returns early and the VE sweep is skipped until tomorrow.
    /// </summary>
    [Fact]
    public async Task OnlyOneOfTheTwoSweepsSucceededThisSlot_TheTickStillRuns()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await SeedHistoryAsync(harness, JobSchedules.LicenseWatch, success: true, InThisSlot);
        await SeedHistoryAsync(harness, JobSchedules.VeLicenseWatch, success: false, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var written = await RowsWrittenAsync(harness);
        Assert.Contains(written, h => h.JobName == JobSchedules.LicenseWatch);
        Assert.Contains(written, h => h.JobName == JobSchedules.VeLicenseWatch);
    }

    // ---- The behaviour the guard exists for -------------------------------------------------

    [Fact]
    public async Task BothSweepsSucceededThisSlot_TheTickSkips()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await SeedHistoryAsync(harness, JobSchedules.LicenseWatch, success: true, InThisSlot);
        await SeedHistoryAsync(harness, JobSchedules.VeLicenseWatch, success: true, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Empty(await RowsWrittenAsync(harness));
        Assert.Equal(0, uls.Calls);
    }

    [Fact]
    public async Task NeitherSweepHasRunThisSlot_TheTickRuns()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var written = await RowsWrittenAsync(harness);
        Assert.Contains(written, h => h.JobName == JobSchedules.LicenseWatch);
        Assert.Contains(written, h => h.JobName == JobSchedules.VeLicenseWatch);
    }

    /// <summary>
    /// Yesterday's successes must not satisfy today's slot — otherwise the anchor would fire once and
    /// never again, which is the failure the whole slot mechanism replaced.
    /// </summary>
    [Fact]
    public async Task SuccessesFromBeforeTheDueSlot_DoNotCount()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await SeedHistoryAsync(harness, JobSchedules.LicenseWatch, success: true, BeforeThisSlot);
        await SeedHistoryAsync(harness, JobSchedules.VeLicenseWatch, success: true, BeforeThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.NotEmpty(await RowsWrittenAsync(harness));
    }

    /// <summary>
    /// A failed pair does not satisfy the guard either — the point of anchoring is that a slot with
    /// no <i>successful</i> run is retried on the next tick.
    /// </summary>
    [Fact]
    public async Task BothSweepsFailedThisSlot_TheTickRunsAgain()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await SeedHistoryAsync(harness, JobSchedules.LicenseWatch, success: false, InThisSlot);
        await SeedHistoryAsync(harness, JobSchedules.VeLicenseWatch, success: false, InThisSlot);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.NotEmpty(await RowsWrittenAsync(harness));
    }

    /// <summary>
    /// Non-vacuity for the skip case: the seeding helper and the "rows written" query must actually
    /// be able to disagree, or <c>BothSweepsSucceededThisSlot_TheTickSkips</c> could pass by looking
    /// at nothing.
    /// </summary>
    [Fact]
    public async Task TheRowsWrittenProbe_SeesRowsWhenThereAreSome()
    {
        var uls = new CountingUlsClient();
        await using var harness = await CreateHarnessAsync(uls);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(2, (await RowsWrittenAsync(harness)).Count);
    }
}
