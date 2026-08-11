using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// What <c>JobRunHistoryLogger</c>'s failure-path <c>ChangeTracker.Clear()</c> actually detaches —
/// the question left open on issue #232.
///
/// <para><b>The finding.</b> The clear does exactly what it says: everything is detached. But
/// <c>TryCompleteHistoryAsync</c> then calls <c>JobRunHistories.Attach(history)</c>, and
/// <c>Attach</c> takes the whole <b>entity graph</b>. When the caller passed a real
/// <c>teamId</c>, EF's relationship fixup had already populated <c>history.Team</c> while the team
/// was tracked — so attaching the history row <b>drags the team back in</b> as <c>Unchanged</c>, and
/// a caller that mutates it afterwards still saves successfully.</para>
///
/// <para>That is why #232 did not reproduce: <c>SessionIngestionJob</c> passes <c>team.Id</c>, so its
/// throttle stamp survived — <b>by accident of graph attachment, not by design</b>. Pass
/// <c>teamId: null</c>, as <c>LicenseWatchJob</c> does, and nothing re-attaches; a tracked entity
/// mutated after a failed step is silently not saved.</para>
///
/// <para>So the audit's mechanism was right, its instance was wrong, and the
/// <c>ExecuteUpdateAsync</c> that replaced the tracked write is justified on a better ground than the
/// one it shipped with: it does not care what the tracker does. These tests exist to stop the
/// accident being mistaken for a guarantee.</para>
/// </summary>
public class JobRunHistoryLoggerTrackerTests
{
    private static async Task<WorkerTickHarness> CreateHarnessAsync() =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<JobRunHistoryLogger>();
        });

    /// <summary>
    /// The accident, pinned. Not a behaviour to rely on — a behaviour to know about, because it is
    /// the difference between #232 reproducing and not.
    /// </summary>
    [Fact]
    public async Task OnFailure_WithATeamId_TheTeamIsReAttachedByTheHistoryRowsNavigation()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.SeedTeamAsync("HRCC");

        using var scope = harness.ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

        var team = await dbContext.Teams.FirstAsync();

        await logger.RunAsync("Probe", _ => throw new InvalidOperationException("boom"), team.Id, CancellationToken.None);

        // ChangeTracker.Entries, never dbContext.Entry(): Entry() BEGINS TRACKING an untracked
        // entity and reports Unchanged, so it can never answer "is this detached?".
        Assert.Contains(dbContext.ChangeTracker.Entries<Team>(), e => ReferenceEquals(e.Entity, team));

        // The route back in: fixup populated this while the team was tracked, and Attach follows it.
        var history = dbContext.ChangeTracker.Entries<JobRunHistory>().Single().Entity;
        Assert.NotNull(history.Team);
    }

    /// <summary>
    /// The same failure with no team id, which is how <c>LicenseWatchJob</c> and the ULS watcher call
    /// it. Nothing links the tracked entity to the history row, so nothing brings it back — and a
    /// write made after the failed step is <b>silently lost</b>, returning 0 from
    /// <c>SaveChangesAsync</c> with the old value left in the database.
    ///
    /// <para>This is the shape #232 described. It is real; it just is not what
    /// <c>SessionIngestionJob</c> was doing.</para>
    /// </summary>
    [Fact]
    public async Task OnFailure_WithoutATeamId_AnUnrelatedTrackedEntityIsDetachedAndItsWriteIsLost()
    {
        await using var harness = await CreateHarnessAsync();
        var seeded = await harness.SeedTeamAsync("HRCC");

        using var scope = harness.ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

        var team = await dbContext.Teams.FirstAsync();
        var stampedAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

        await logger.RunAsync("Probe", _ => throw new InvalidOperationException("boom"), null, CancellationToken.None);

        Assert.DoesNotContain(dbContext.ChangeTracker.Entries<Team>(), e => ReferenceEquals(e.Entity, team));

        // The silent part: no exception, and SaveChangesAsync reports nothing written.
        team.LastIngestionRunUtc = stampedAt;
        var written = await dbContext.SaveChangesAsync();
        Assert.Equal(0, written);

        await using var verify = harness.NewContext();
        var persisted = await verify.Teams.AsNoTracking().SingleAsync(t => t.Id == seeded.Id);
        Assert.Null(persisted.LastIngestionRunUtc);
    }

    /// <summary>
    /// And the reason the shipped fix is right regardless of any of the above: an
    /// <c>ExecuteUpdateAsync</c> is a statement sent to the database, so it cannot be undone by
    /// whatever the change tracker happens to be doing.
    /// </summary>
    [Fact]
    public async Task AnExecuteUpdate_PersistsEvenAfterTheEntityHasBeenDetached()
    {
        await using var harness = await CreateHarnessAsync();
        var seeded = await harness.SeedTeamAsync("HRCC");

        using var scope = harness.ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
        var stampedAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

        await dbContext.Teams.FirstAsync();
        await logger.RunAsync("Probe", _ => throw new InvalidOperationException("boom"), null, CancellationToken.None);

        await dbContext.Teams
            .Where(t => t.Id == seeded.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastIngestionRunUtc, stampedAt));

        await using var verify = harness.NewContext();
        var persisted = await verify.Teams.AsNoTracking().SingleAsync(t => t.Id == seeded.Id);
        Assert.Equal(stampedAt, persisted.LastIngestionRunUtc);
    }
}
