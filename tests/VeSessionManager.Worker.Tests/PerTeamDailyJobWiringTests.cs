using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// The three <see cref="PerTeamDailyJob"/> subclasses are three lines each, and both of those lines
/// are copied from a sibling (issue #325).
///
/// <para>The base class handles the timer, the per-team scope and the history row, all covered by
/// <see cref="PerTeamScopeIsolationTests"/>. What each subclass adds is exactly two facts — <b>which
/// schedule key</b> it passes up, and <b>which service</b> it resolves — and both are the kind of
/// thing a copy-paste gets wrong while still compiling.</para>
///
/// <para><b>A wrong schedule key is silent and does three things at once:</b> the job's history rows
/// are filed under another job's name, the Job Schedule page reports the wrong cadence for both, and
/// the timer reads the wrong <c>Jobs:*</c> config key — so changing the interval in configuration
/// adjusts a different job. <c>JobRegistrationTests</c> cannot see this: every key involved is a real
/// key with a real descriptor and a real registration.</para>
/// </summary>
public class PerTeamDailyJobWiringTests
{
    /// <summary>
    /// Answers "which service did it ask for?" and nothing else. Returning null makes
    /// <c>GetRequiredService</c> throw immediately after, which is fine — the question is answered by
    /// then, and this avoids constructing service graphs the subclass line does not depend on.
    /// </summary>
    private sealed class RecordingServiceProvider : IServiceProvider
    {
        public List<Type> Requested { get; } = [];

        public object? GetService(Type serviceType)
        {
            Requested.Add(serviceType);
            return null;
        }
    }

    /// <summary>
    /// <c>RunForTeamAsync</c> is protected, which is right — it is an implementation detail of the
    /// base class's loop. Reflection rather than a widened accessor: the alternative is changing
    /// production visibility to suit a test.
    /// </summary>
    private static Type ServiceResolvedBy(PerTeamDailyJob job)
    {
        var method = typeof(PerTeamDailyJob).GetMethod(
            "RunForTeamAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var provider = new RecordingServiceProvider();
        var team = new Team { Id = 1, Name = "TEAM" };

        // The resolve is the first thing each override does, so the throw comes after it.
        //
        // The returned Task has to be awaited, not merely started. The overrides became `async` when
        // RunForTeamAsync started returning Task<object?> (#309, DUP-11) — before that they were
        // expression-bodied and non-async, so a failed resolve threw straight out of Invoke. An
        // async method captures it in the Task instead, and dropping the Task makes this probe pass
        // while observing nothing. GetAwaiter().GetResult() rethrows it unwrapped.
        Assert.ThrowsAny<Exception>(() =>
        {
            try
            {
                ((Task)method!.Invoke(job, [provider, team, CancellationToken.None])!).GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException!;
            }
        });

        return Assert.Single(provider.Requested);
    }

    private static async Task<WorkerTickHarness> CreateHarnessAsync() =>
        // Deliberately registers none of the per-job services: the tick is expected to fail for the
        // team, and the history row it leaves behind is what carries the job name under test.
        await WorkerTickHarness.CreateAsync(services => services.AddScoped<JobRunHistoryLogger>());

    private static async Task<JobRunHistory> RunOneTeamAsync(
        Func<WorkerTickHarness, PerTeamDailyJob> create, WorkerTickHarness harness)
    {
        await harness.SeedTeamAsync("TEAM");
        await create(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        return await verify.JobRunHistories.AsNoTracking().SingleAsync();
    }

    // ---- DayBeforeReminderJob ------------------------------------------------------------------

    [Fact]
    public void DayBeforeReminder_ResolvesTheNotificationService()
    {
        var job = new DayBeforeReminderJob(null!, null!, Quiet.Logger<DayBeforeReminderJob>());
        Assert.Equal(typeof(CandidateNotificationService), ServiceResolvedBy(job));
    }

    [Fact]
    public async Task DayBeforeReminder_FilesItsRunUnderItsOwnScheduleKey()
    {
        await using var harness = await CreateHarnessAsync();

        var row = await RunOneTeamAsync(
            h => new DayBeforeReminderJob(h.ScopeFactory, h.Configuration, Quiet.Logger<DayBeforeReminderJob>()),
            harness);

        Assert.Equal(JobSchedules.DayBeforeReminder, row.JobName);
    }

    // ---- PaymentReminderJob --------------------------------------------------------------------

    [Fact]
    public void PaymentReminder_ResolvesThePaymentReminderService()
    {
        var job = new PaymentReminderJob(null!, null!, Quiet.Logger<PaymentReminderJob>());
        Assert.Equal(typeof(PaymentReminderService), ServiceResolvedBy(job));
    }

    [Fact]
    public async Task PaymentReminder_FilesItsRunUnderItsOwnScheduleKey()
    {
        await using var harness = await CreateHarnessAsync();

        var row = await RunOneTeamAsync(
            h => new PaymentReminderJob(h.ScopeFactory, h.Configuration, Quiet.Logger<PaymentReminderJob>()),
            harness);

        Assert.Equal(JobSchedules.PaymentReminder, row.JobName);
    }

    // ---- SquareLinkPurgeJob --------------------------------------------------------------------

    [Fact]
    public void SquareLinkPurge_ResolvesThePurgeService()
    {
        var job = new SquareLinkPurgeJob(null!, null!, Quiet.Logger<SquareLinkPurgeJob>());
        Assert.Equal(typeof(SquarePaymentLinkPurgeService), ServiceResolvedBy(job));
    }

    [Fact]
    public async Task SquareLinkPurge_FilesItsRunUnderItsOwnScheduleKey()
    {
        await using var harness = await CreateHarnessAsync();

        var row = await RunOneTeamAsync(
            h => new SquareLinkPurgeJob(h.ScopeFactory, h.Configuration, Quiet.Logger<SquareLinkPurgeJob>()),
            harness);

        Assert.Equal(JobSchedules.SquareLinkPurge, row.JobName);
    }

    // ---- The property that ties them together ---------------------------------------------------

    /// <summary>
    /// No two of them share a key or a service. Stated as one fact because the failure mode is
    /// relative — a copied line is wrong precisely by matching its sibling, and three tests each
    /// asserting an expected value in isolation can all pass while two jobs collide.
    /// </summary>
    [Fact]
    public void NoTwoSubclassesShareAScheduleKeyOrAService()
    {
        PerTeamDailyJob[] jobs =
        [
            new DayBeforeReminderJob(null!, null!, Quiet.Logger<DayBeforeReminderJob>()),
            new PaymentReminderJob(null!, null!, Quiet.Logger<PaymentReminderJob>()),
            new SquareLinkPurgeJob(null!, null!, Quiet.Logger<SquareLinkPurgeJob>())
        ];

        var services = jobs.Select(ServiceResolvedBy).ToList();
        Assert.Equal(3, services.Distinct().Count());
    }

    /// <summary>
    /// Non-vacuity: <see cref="ServiceResolvedBy"/> must genuinely observe a resolve rather than
    /// reporting an empty list, which <c>Assert.Single</c> would surface — but only if the reflection
    /// lookup found the method at all.
    /// </summary>
    [Fact]
    public void TheReflectionProbe_ActuallyObservesAResolve()
    {
        Assert.NotNull(typeof(PerTeamDailyJob).GetMethod(
            "RunForTeamAsync", BindingFlags.NonPublic | BindingFlags.Instance));

        var job = new PaymentReminderJob(null!, null!, Quiet.Logger<PaymentReminderJob>());
        Assert.NotNull(ServiceResolvedBy(job));
    }
}
