using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.PiiPurge;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// The two remaining hand-written ticks: <see cref="HistoricalImportJob"/> and
/// <see cref="PiiPurgeJob"/> (issue #325).
///
/// <para>Neither is per-team, so <see cref="PerTeamScopeIsolationTests"/> does not reach them, and
/// both are short enough to look obviously correct — which is the usual reason a job goes untested
/// until it is not.</para>
/// </summary>
public class QueueDrainAndPurgeJobTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Empty feeds: enough for an import to complete, finding nothing to import.</summary>
    private sealed class EmptyExamToolsClient : IExamToolsClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(
            ExamToolsCredentials c, DateOnly s, DateOnly e, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);
        }

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExamToolsApplicant>>([]);

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) =>
            Task.FromResult<ExamToolsApplicantDetail?>(null);
    }

    private static async Task<WorkerTickHarness> CreateImportHarnessAsync(EmptyExamToolsClient examTools) =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IExamToolsClient>(examTools);
            services.AddSingleton<IOptions<ExamToolsOptions>>(
                Options.Create(new ExamToolsOptions { BaseUrl = "https://exam.tools" }));
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<SessionIngestionService>();
            services.AddScoped<VolunteerExaminerSyncService>();
            services.AddScoped<HistoricalImportService>();
        });

    private static async Task<List<JobRunHistory>> HistoryAsync(WorkerTickHarness harness, string jobName)
    {
        await using var verify = harness.NewContext();
        return await verify.JobRunHistories.AsNoTracking()
            .Where(h => h.JobName == jobName).ToListAsync();
    }

    // ---- HistoricalImportJob: peek before logging ----------------------------------------------

    /// <summary>
    /// <b>The property the peek exists for.</b> This job ticks every few seconds so a queued import
    /// starts promptly, and the queue is empty essentially always. Writing a history row per check
    /// would bury the ops dashboard under a row a minute and destroy the "silence means nothing
    /// happened" property every other job here relies on.
    /// </summary>
    [Fact]
    public async Task AnEmptyQueue_WritesNoHistoryRowAtAll()
    {
        var examTools = new EmptyExamToolsClient();
        await using var harness = await CreateImportHarnessAsync(examTools);

        await new HistoricalImportJob(harness.ScopeFactory, harness.Configuration,
            Quiet.Logger<HistoricalImportJob>()).RunTickAsync(CancellationToken.None);

        Assert.Empty(await HistoryAsync(harness, JobSchedules.HistoricalImport));
        Assert.Equal(0, examTools.Calls);
    }

    /// <summary>
    /// The other half, and the one that keeps the test above honest: with work queued, a row appears.
    /// A tick that returned early unconditionally would satisfy the empty case perfectly.
    /// </summary>
    [Fact]
    public async Task APendingRequest_IsRunAndRecorded()
    {
        var examTools = new EmptyExamToolsClient();
        await using var harness = await CreateImportHarnessAsync(examTools);

        var team = await SeedConfiguredTeamAsync(harness);
        await SeedRequestAsync(harness, team.Id, HistoricalImportStatus.Pending, startedUtc: null);

        await new HistoricalImportJob(harness.ScopeFactory, harness.Configuration,
            Quiet.Logger<HistoricalImportJob>()).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness, JobSchedules.HistoricalImport));
        Assert.True(row.Success);

        // teamId null on purpose: the request row carries its own team, and this job step is the
        // queue drain rather than work done on one team's behalf.
        Assert.Null(row.TeamId);

        await using var verify = harness.NewContext();
        var request = await verify.HistoricalImportRequests.AsNoTracking().SingleAsync();
        Assert.Equal(HistoricalImportStatus.Completed, request.Status);
    }

    /// <summary>
    /// A request left <c>Running</c> past the stale threshold is a Worker that died mid-import, and
    /// it must be picked up again — otherwise one restart strands an admin's import forever, with the
    /// queue reporting it as in progress.
    /// </summary>
    [Fact]
    public async Task AStaleRunningRequest_IsResumedRatherThanStranded()
    {
        await using var harness = await CreateImportHarnessAsync(new EmptyExamToolsClient());

        var team = await SeedConfiguredTeamAsync(harness);
        await SeedRequestAsync(harness, team.Id, HistoricalImportStatus.Running,
            startedUtc: Now - HistoricalImportService.StaleRunningThreshold - TimeSpan.FromMinutes(1));

        await new HistoricalImportJob(harness.ScopeFactory, harness.Configuration,
            Quiet.Logger<HistoricalImportJob>()).RunTickAsync(CancellationToken.None);

        Assert.Single(await HistoryAsync(harness, JobSchedules.HistoricalImport));
    }

    /// <summary>
    /// A request that started recently is someone else's in-flight work, not abandoned. Treating it
    /// as stale would run two imports over the same range at once.
    /// </summary>
    [Fact]
    public async Task AFreshlyRunningRequest_IsLeftAlone()
    {
        await using var harness = await CreateImportHarnessAsync(new EmptyExamToolsClient());

        var team = await SeedConfiguredTeamAsync(harness);
        await SeedRequestAsync(harness, team.Id, HistoricalImportStatus.Running,
            startedUtc: Now - TimeSpan.FromSeconds(30));

        await new HistoricalImportJob(harness.ScopeFactory, harness.Configuration,
            Quiet.Logger<HistoricalImportJob>()).RunTickAsync(CancellationToken.None);

        Assert.Empty(await HistoryAsync(harness, JobSchedules.HistoricalImport));
    }

    // ---- PiiPurgeJob ----------------------------------------------------------------------------

    /// <summary>
    /// Global rather than per-team, so unlike every job around it there is no team loop and the
    /// history row carries no team id. Worth pinning: the row is the only evidence the retention
    /// obligation is being met at all, and a purge that silently stops running looks exactly like a
    /// purge with nothing to do.
    /// </summary>
    [Fact]
    public async Task PiiPurge_RecordsOneGlobalRunPerTick()
    {
        await using var harness = await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddScoped<SystemSettingsService>();
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<PiiPurgeService>();
            // The tick also sweeps spent self-service tokens (#303, D-03), so the job resolves this.
            // Registered here rather than stubbed: PurgeSpentTokensAsync is a real ExecuteDeleteAsync
            // against the harness's real SQLite, which is the whole reason that sweep lives at the
            // job layer and not inside PiiPurgeService (whose own tests are InMemory).
            services.AddScoped<IEmailSender, NullEmailSender>();
            services.AddScoped<VeSelfServiceLinkService>();
        });

        // Two teams, to show the tick does not fan out over them the way its neighbours do.
        await harness.SeedTeamAsync("TEAMA");
        await harness.SeedTeamAsync("TEAMB");

        await new PiiPurgeJob(harness.ScopeFactory, harness.Configuration,
            Quiet.Logger<PiiPurgeJob>()).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness, JobSchedules.PiiPurge));
        Assert.True(row.Success);
        Assert.Null(row.TeamId);
        Assert.False(string.IsNullOrWhiteSpace(row.ResultSummary));
    }

    // ---- Fixtures --------------------------------------------------------------------------------

    private static async Task<Team> SeedConfiguredTeamAsync(WorkerTickHarness harness)
    {
        await using var dbContext = harness.NewContext();
        var team = new Team
        {
            Name = "TEAMA",
            ExamToolsTeamCode = "TEAMA",
            ExamToolsUsername = "ve@example.com",
            ExamToolsPassword = "secret",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>The request's RequestedByUserId is a real foreign key, so a row has to exist.</summary>
    private static async Task<int> RequestingUserIdAsync(WorkerTickHarness harness)
    {
        await using var dbContext = harness.NewContext();
        var existing = await dbContext.Users.FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing.Id;
        }

        var user = new User { Name = "Admin", Email = "admin@localhost", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private static async Task SeedRequestAsync(
        WorkerTickHarness harness, int teamId, HistoricalImportStatus status, DateTime? startedUtc)
    {
        await using var dbContext = harness.NewContext();
        dbContext.HistoricalImportRequests.Add(new HistoricalImportRequest
        {
            TeamId = teamId,
            Status = status,
            StartedUtc = startedUtc,
            RequestedUtc = Now - TimeSpan.FromHours(1),
            RequestedByUserId = await RequestingUserIdAsync(harness),
            StartDate = new DateOnly(2026, 7, 1),
            EndDate = new DateOnly(2026, 7, 31)
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>The purge never sends anything; this exists only to satisfy the link service's own
    /// constructor.</summary>
    private sealed class NullEmailSender : IEmailSender
    {
        public bool IsConfigured => false;

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
