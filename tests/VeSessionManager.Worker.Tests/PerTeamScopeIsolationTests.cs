using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// Issue #292: every per-team job created <b>one scope per tick</b>, so all teams shared one
/// <see cref="AppDbContext"/> and one change tracker for the whole run.
///
/// <para>The consequence is that <c>JobRunHistoryLogger</c> calls <c>ChangeTracker.Clear()</c> when a
/// team's step fails, and with a shared scope that clear reaches every <i>other</i> team's pending
/// state in the same tick.</para>
///
/// <para><b>What these tests do and do not prove, stated plainly.</b> The assertion that
/// discriminates is <b>scope identity</b> — three teams, three contexts. Verified by reverting the
/// production code: the old shape yields one context and this fails.</para>
///
/// <para>The audit-row assertions below pass under <i>both</i> shapes, and are kept as containment
/// documentation rather than as proof of data loss. The reason is worth knowing: every real step
/// saves per item, so by the time a later team fails the earlier team's work is already committed and
/// a tracker clear cannot reach it. Per-team scopes therefore remove a <i>latent</i> coupling — the
/// day a step batches its writes, or a save is added after the loop, the shared scope turns that into
/// silent loss — plus the unbounded tracker growth across a tick. Claiming they prove more than that
/// would be an overclaim, and this comment exists because the first draft of it did.</para>
///
/// <para><see cref="PerTeamDailyJob"/> is the right vehicle: it is the abstract base three jobs
/// share, and its one abstract member lets a test make exactly one team fail without standing up an
/// entire pipeline.</para>
/// </summary>
public class PerTeamScopeIsolationTests
{
    /// <summary>
    /// Writes a marker row per team, and throws for one nominated team — the shape of "team B's step
    /// failed" without needing ExamTools, Zoom, Square or any of the real collaborators.
    /// </summary>
    private sealed class ProbeJob(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        string failForTeamNamed)
        : PerTeamDailyJob(scopeFactory, configuration, NullLogger<ProbeJob>.Instance, JobSchedules.PiiPurge)
    {
        /// <summary>Which DbContext instance served each team, by reference identity.</summary>
        public List<AppDbContext> ContextsSeen { get; } = [];

        protected override async Task RunForTeamAsync(
            IServiceProvider scopedServices, Team team, CancellationToken cancellationToken)
        {
            var dbContext = scopedServices.GetRequiredService<AppDbContext>();
            ContextsSeen.Add(dbContext);

            // A pending write, made before the failure, exactly as a real step would.
            dbContext.AuditLogs.Add(new AuditLog
            {
                UserId = null,
                Action = "ProbeTouched",
                EntityType = nameof(Team),
                EntityId = team.Id,
                Details = team.Name,
                TimestampUtc = DateTime.UtcNow
            });

            if (team.Name == failForTeamNamed)
            {
                throw new InvalidOperationException($"boom for {team.Name}");
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task EachTeamGetsItsOwnScope_SoAFailureCannotDiscardAnotherTeamsWork()
    {
        await using var harness = await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddScoped<JobRunHistoryLogger>();
            services.AddSingleton(TimeProvider.System);
        });

        await harness.SeedTeamAsync("AlphaTeam");
        await harness.SeedTeamAsync("BravoTeam");   // fails
        await harness.SeedTeamAsync("CharlieTeam");

        var job = new ProbeJob(harness.ScopeFactory, harness.Configuration, failForTeamNamed: "BravoTeam");
        await job.RunTickAsync(CancellationToken.None);

        // **The assertion that discriminates.** A distinct context per team; the old
        // one-scope-per-tick shape yields one instance for all three, which is what let a single
        // ChangeTracker.Clear() reach the others' pending state.
        Assert.Equal(3, job.ContextsSeen.Count);
        Assert.Equal(3, job.ContextsSeen.Distinct().Count());

        await using var verify = harness.NewContext();
        var touched = await verify.AuditLogs
            .Where(a => a.Action == "ProbeTouched")
            .Select(a => a.Details)
            .ToListAsync();

        // Containment, not proof of the old bug: these hold under both shapes, because each step
        // saves before the next team runs. See the class remarks — the value of per-team scopes is
        // removing the coupling, not repairing loss that is already prevented by per-item saves.
        Assert.Contains("AlphaTeam", touched);
        Assert.Contains("CharlieTeam", touched);
        Assert.DoesNotContain("BravoTeam", touched);   // threw before its own save
    }

    /// <summary>
    /// The failure is contained, not swallowed: it still reaches JobRunHistory as a failed run for
    /// that team, which is the whole reason per-team history rows exist.
    /// </summary>
    [Fact]
    public async Task TheFailingTeamIsStillRecordedAsAFailedRun()
    {
        await using var harness = await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddScoped<JobRunHistoryLogger>();
            services.AddSingleton(TimeProvider.System);
        });

        var alpha = await harness.SeedTeamAsync("AlphaTeam");
        var bravo = await harness.SeedTeamAsync("BravoTeam");

        var job = new ProbeJob(harness.ScopeFactory, harness.Configuration, failForTeamNamed: "BravoTeam");
        await job.RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var history = await verify.JobRunHistories.OrderBy(h => h.Id).ToListAsync();

        Assert.Equal(2, history.Count);
        Assert.True(history.Single(h => h.TeamId == alpha.Id).Success);
        Assert.False(history.Single(h => h.TeamId == bravo.Id).Success);
    }

    /// <summary>
    /// A tick must not die on the whole run because one team threw — that is what would take the
    /// host down, since RunTickAsync is what JobTick wraps.
    /// </summary>
    [Fact]
    public async Task OneTeamThrowing_DoesNotAbortTheRemainingTeams()
    {
        await using var harness = await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddScoped<JobRunHistoryLogger>();
            services.AddSingleton(TimeProvider.System);
        });

        await harness.SeedTeamAsync("AlphaTeam");
        await harness.SeedTeamAsync("BravoTeam");
        await harness.SeedTeamAsync("CharlieTeam");

        var job = new ProbeJob(harness.ScopeFactory, harness.Configuration, failForTeamNamed: "AlphaTeam");

        // Completing without throwing is the assertion; the count confirms it kept going rather than
        // exiting quietly after the first team.
        await job.RunTickAsync(CancellationToken.None);

        Assert.Equal(3, job.ContextsSeen.Count);
    }
}
