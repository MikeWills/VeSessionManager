using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Reconciliation;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <see cref="ReconciliationJob"/>'s per-team loop (issue #325).
///
/// <para>This job is itself a monitor — it exists because the suite could not catch the
/// last-day-of-the-month import bug, since every fake shared the same wrong assumption. That is a
/// reason to test its <i>bookkeeping</i> carefully rather than a reason not to test it: if the loop
/// silently skips a team or loses its summary, the monitor reports green while checking nothing, and
/// there is no second monitor watching this one.</para>
/// </summary>
public class ReconciliationJobTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Answers the one call the service makes. Records which teams it was asked about, by team code —
    /// the only way to see from outside whether the loop reached a team at all.
    /// </summary>
    private sealed class RecordingExamToolsClient : IExamToolsClient
    {
        public List<string> TeamCodesAsked { get; } = [];
        public bool IsConfigured => true;

        /// <summary>
        /// Runs while the job is inside the loop, which is the only way to produce the interleaving
        /// the deleted-team guard exists for: the teams are listed in one scope and each is re-read in
        /// another, so a delete has to land between those two reads to be the case under test.
        /// </summary>
        public Action<string>? DuringFirstCall { get; set; }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(
            ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateInclusiveUtc,
            CancellationToken cancellationToken)
        {
            TeamCodesAsked.Add(credentials.TeamCode);
            if (TeamCodesAsked.Count == 1)
            {
                DuringFirstCall?.Invoke(credentials.TeamCode);
            }

            return Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct) =>
            throw new NotSupportedException("reconciliation only reads the closed-session feed");

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static async Task<WorkerTickHarness> CreateHarnessAsync(RecordingExamToolsClient examTools) =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IExamToolsClient>(examTools);
            services.AddSingleton<IOptions<ExamToolsOptions>>(
                Options.Create(new ExamToolsOptions { BaseUrl = "https://exam.tools" }));
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<ReconciliationService>();
        });

    private static ReconciliationJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, harness.Configuration, Quiet.Logger<ReconciliationJob>());

    private static async Task<Team> SeedConfiguredTeamAsync(WorkerTickHarness harness, string name)
    {
        await using var dbContext = harness.NewContext();
        var team = new Team
        {
            Name = name,
            ExamToolsTeamCode = name,
            ExamToolsUsername = "ve@example.com",
            ExamToolsPassword = "secret",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<List<JobRunHistory>> HistoryAsync(WorkerTickHarness harness)
    {
        await using var verify = harness.NewContext();
        return await verify.JobRunHistories.AsNoTracking()
            .Where(h => h.JobName == JobSchedules.Reconciliation)
            .OrderBy(h => h.Id).ToListAsync();
    }

    // ---- One row per team, carrying that team's id -------------------------------------------

    /// <summary>
    /// Per team, not per tick, and the row says which team. One team's expired credentials must not
    /// hide another team's clean sweep, and the ops dashboard has to be able to name the team that
    /// drifted — a single merged row cannot do either.
    /// </summary>
    [Fact]
    public async Task EveryTeamGetsItsOwnHistoryRow_TaggedWithItsTeamId()
    {
        var examTools = new RecordingExamToolsClient();
        await using var harness = await CreateHarnessAsync(examTools);

        var a = await SeedConfiguredTeamAsync(harness, "TEAMA");
        var b = await SeedConfiguredTeamAsync(harness, "TEAMB");

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var history = await HistoryAsync(harness);
        Assert.Equal(2, history.Count);
        Assert.Equal([a.Id, b.Id], [.. history.Select(h => h.TeamId)]);
        Assert.All(history, h => Assert.True(h.Success));
        Assert.Equal(["TEAMA", "TEAMB"], examTools.TeamCodesAsked);
    }

    /// <summary>
    /// <b>The overload-resolution property.</b> <c>RunAsync</c> has a result-returning overload and a
    /// void one, and only the first records <c>ResultSummary</c>. Binding a method group to the wrong
    /// one compiles cleanly and leaves every summary silently null — the job stays green and the
    /// dashboard stops saying what it found, which for a monitor is the whole output.
    /// </summary>
    [Fact]
    public async Task TheRunSummaryReachesTheHistoryRow()
    {
        await using var harness = await CreateHarnessAsync(new RecordingExamToolsClient());
        await SeedConfiguredTeamAsync(harness, "TEAMA");

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness));
        Assert.False(string.IsNullOrWhiteSpace(row.ResultSummary));
    }

    // ---- Scope isolation ----------------------------------------------------------------------

    /// <summary>
    /// This job has its own copy of the per-team loop rather than deriving from
    /// <see cref="PerTeamDailyJob"/>, so <see cref="PerTeamScopeIsolationTests"/> does not cover it.
    /// The two copies were fixed together in #292, which is exactly the situation where one gets
    /// reverted alone.
    ///
    /// <para>Observed through the change tracker: a per-tick scope would leave every team's
    /// <c>ReconciliationFinding</c>/history entities tracked in one context for the whole run, so the
    /// second team's context would still be holding the first team's rows.</para>
    /// </summary>
    [Fact]
    public async Task EachTeamIsProcessedInItsOwnScope()
    {
        await using var harness = await CreateHarnessAsync(new RecordingExamToolsClient());
        await SeedConfiguredTeamAsync(harness, "TEAMA");
        await SeedConfiguredTeamAsync(harness, "TEAMB");

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        // Each scope wrote exactly one history row, so no scope can have seen more than one team's.
        var history = await HistoryAsync(harness);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history.Select(h => h.TeamId).Distinct().Count());
    }

    // ---- Edges ---------------------------------------------------------------------------------

    /// <summary>
    /// The teams are listed in one scope and re-read in another, so a team deleted in between is a
    /// real interleaving rather than a hypothetical — Team Maintenance can delete one while the job
    /// runs. It must be skipped, not throw: an unguarded throw here reaches <c>JobTick</c>, and
    /// before that existed it would have stopped the whole Worker.
    ///
    /// <para>The delete is fired from inside the first team's API call, because that is the only
    /// point that lies between the two reads. Deleting before the tick instead proves nothing — the
    /// team simply never appears in the list, and the test passes with the guard removed. It did, on
    /// the first draft of this test.</para>
    /// </summary>
    [Fact]
    public async Task ATeamDeletedBetweenTheListAndTheReRead_IsSkippedAndTheRestStillRun()
    {
        var examTools = new RecordingExamToolsClient();
        await using var harness = await CreateHarnessAsync(examTools);

        var a = await SeedConfiguredTeamAsync(harness, "TEAMA");
        var b = await SeedConfiguredTeamAsync(harness, "TEAMB");

        // Whichever team the loop reaches first, delete the *other* one — the one it has listed and
        // not yet re-read. Deleting the team currently being processed would only prove that
        // JobRunHistory's Restrict foreign key works, which is a different fact entirely.
        var doomedId = 0;
        examTools.DuringFirstCall = code =>
        {
            doomedId = code == "TEAMA" ? b.Id : a.Id;
            using var dbContext = harness.NewContext();
            dbContext.Teams.Where(t => t.Id == doomedId).ExecuteDelete();
        };

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness));
        Assert.NotEqual(doomedId, row.TeamId);
        Assert.Single(examTools.TeamCodesAsked);
    }

    /// <summary>
    /// A team with no ExamTools credentials is skipped by the service, quietly and successfully —
    /// the optional-integration pattern. It still gets a history row, because "checked, nothing to
    /// do" and "never checked" must not look the same on the dashboard.
    /// </summary>
    [Fact]
    public async Task AnUnconfiguredTeam_StillGetsARowAndMakesNoApiCall()
    {
        var examTools = new RecordingExamToolsClient();
        await using var harness = await CreateHarnessAsync(examTools);
        await harness.SeedTeamAsync("NOCREDS");

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness));
        Assert.True(row.Success);
        Assert.Empty(examTools.TeamCodesAsked);
    }

    [Fact]
    public async Task NoTeamsAtAll_WritesNothingAndDoesNotThrow()
    {
        await using var harness = await CreateHarnessAsync(new RecordingExamToolsClient());

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Empty(await HistoryAsync(harness));
    }
}
