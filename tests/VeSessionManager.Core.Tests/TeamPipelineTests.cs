using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.VolunteerExaminers;
using VeSessionManager.Core.Zoom;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// TeamPipeline — the single definition of the per-team refresh order, extracted 2026-08-05 from
/// three copies that had already drifted (see docs/team-maintenance.md).
///
/// <para>Built from the <b>real</b> services with fake clients, the same way the rest of this suite
/// works, rather than with a mocking framework: these services expose no virtual members and
/// implement no interfaces, so a dynamic-proxy mock cannot stub them at all. The clients are where
/// this codebase puts its seams, so that is where the fakes go.</para>
/// </summary>
public class TeamPipelineTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>Returns an empty feed for everything — enough for the pipeline to run end to end.</summary>
    private sealed class EmptyExamToolsClient : IExamToolsClient
    {
        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsApplicant>>([]);

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
            Task.FromResult<ExamToolsApplicantDetail?>(null);
    }

    // Zoom/Discord/Square/Email are gated on per-team configuration, and the test team configures
    // none of them, so these must never be reached. Throwing rather than returning empty makes an
    // unexpected call a failure rather than a silent pass.
    private sealed class UnusedZoomClient : IZoomClient
    {
        public Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials c, ZoomMeetingRequest r, CancellationToken t) => throw new InvalidOperationException("Zoom should not be called for an unconfigured team.");
        public Task UpdateMeetingAsync(ZoomCredentials c, string id, ZoomMeetingRequest r, CancellationToken t) => throw new InvalidOperationException();
        public Task DeleteMeetingAsync(ZoomCredentials c, string id, CancellationToken t) => throw new InvalidOperationException();
        public Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials c, CancellationToken t) => throw new InvalidOperationException();
    }

    private sealed class UnusedDiscordClient : IDiscordEventClient
    {
        public bool IsConfigured => false;
        public Task<DiscordEvent> CreateEventAsync(ulong g, DiscordEventRequest r, CancellationToken t) => throw new InvalidOperationException();
        public Task UpdateEventAsync(ulong g, string id, DiscordEventRequest r, CancellationToken t) => throw new InvalidOperationException();
        public Task DeleteEventAsync(ulong g, string id, CancellationToken t) => throw new InvalidOperationException();
        public Task<IReadOnlyList<DiscordEvent>> ListEventsAsync(ulong g, CancellationToken t) => throw new InvalidOperationException();
    }

    private sealed class UnusedSquareClient : ISquareClient
    {
        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials c, SquarePaymentLinkRequest r, CancellationToken t) => throw new InvalidOperationException();
        public Task CompleteOrderAsync(SquareCredentials c, string orderId, CancellationToken t) => throw new InvalidOperationException();
        public Task DeletePaymentLinkAsync(SquareCredentials c, string linkId, CancellationToken t) => throw new InvalidOperationException();
    }

    private sealed class UnusedEmailSender : IEmailSender
    {
        public Task SendAsync(EmailCredentials c, EmailMessage m, CancellationToken t) => throw new InvalidOperationException("Email should not be sent for a team with no SMTP configured.");
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TeamPipeline CreatePipeline(AppDbContext dbContext, IExamToolsClient? examTools = null)
    {
        var time = new FixedTimeProvider(Now);
        var examToolsClient = examTools ?? new EmptyExamToolsClient();
        var examToolsOptions = Options.Create(new ExamToolsOptions());
        var appOptions = Options.Create(new AppOptions());

        return new TeamPipeline(
            new SessionIngestionService(dbContext, examToolsClient, time, examToolsOptions, NullLogger<SessionIngestionService>.Instance),
            new VolunteerExaminerSyncService(dbContext, examToolsClient, examToolsOptions, time, NullLogger<VolunteerExaminerSyncService>.Instance),
            new ExamResultSyncService(dbContext, examToolsClient, time, examToolsOptions, NullLogger<ExamResultSyncService>.Instance),
            new SessionEventSchedulingService(dbContext, new UnusedZoomClient(), new UnusedDiscordClient(), time, new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<SessionEventSchedulingService>.Instance),
            new PaymentGenerationService(dbContext, new UnusedSquareClient(), time, new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<PaymentGenerationService>.Instance),
            new CandidateNotificationService(
                dbContext,
                new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
                new UnusedEmailSender(), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
                time,
                appOptions,
                NullLogger<CandidateNotificationService>.Instance),
            new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance));
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        // ExamTools configured so ingestion actually runs; every other integration left unset so the
        // remaining steps take their "skip quietly" path.
        var team = new Team
        {
            Name = "WX0TEST",
            ExamToolsTeamCode = "WX0TEST",
            ExamToolsUsername = "user",
            ExamToolsPassword = "pass",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<string[]> StepNamesAsync(AppDbContext dbContext) =>
        await dbContext.JobRunHistories.OrderBy(h => h.Id).Select(h => h.JobName).ToArrayAsync();

    private static readonly string[] ExpectedSteps =
    [
        "SessionIngestion",
        "VeRosterSync",
        "ExamResultSync",
        "SessionEventScheduling",
        "PaymentGeneration",
        "RegistrationConfirmation"
    ];

    // ---- Membership and order --------------------------------------------------------------------

    /// <summary>
    /// The regression this refactor exists to prevent: a step present in one copy of the pipeline and
    /// missing from another. Exam-result sync was absent from the manual path for weeks exactly that
    /// way, with nothing failing.
    /// </summary>
    [Fact]
    public async Task TeamWideRun_RunsEveryStepInOrder()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await CreatePipeline(dbContext).RunAsync(team, jobNamePrefix: string.Empty, onlySessionId: null, CancellationToken.None);

        var actual = await StepNamesAsync(dbContext);
        Assert.Equal(ExpectedSteps, actual);
    }

    [Fact]
    public async Task ManualRun_RunsTheSameStepsWithThePrefix()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await CreatePipeline(dbContext).RunAsync(team, jobNamePrefix: "Manual", onlySessionId: null, CancellationToken.None);

        var expected = ExpectedSteps.Select(s => "Manual" + s).ToArray();
        var actual = await StepNamesAsync(dbContext);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task EveryStepRecordsItsOwnResultSummary()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await CreatePipeline(dbContext).RunAsync(team, string.Empty, null, CancellationToken.None);

        // Each step is passed as a result-returning delegate so JobRunHistoryLogger's generic
        // overload binds; a void-bound step would leave this null and the dashboard uninformative.
        Assert.All(await dbContext.JobRunHistories.ToListAsync(), h => Assert.False(string.IsNullOrWhiteSpace(h.ResultSummary)));
    }

    // ---- The branch: session-scoped steps use different methods ----------------------------------

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team)
    {
        var vec = new Vec { Name = "TESTVEC" };
        var session = new Session
        {
            ExamToolsSessionId = "session-not-in-feed",
            Title = "September Session",
            ScheduledStartUtc = Now.AddDays(20),
            DurationMinutes = 120,
            Vec = vec,
            Team = team,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Proves the team-wide branch really calls SessionIngestionService.RunAsync: a session missing
    /// from the feed IS the cancellation signal, so it must be cancelled here.
    /// </summary>
    [Fact]
    public async Task TeamWideRun_CancelsASessionMissingFromTheFeed()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);

        await CreatePipeline(dbContext).RunAsync(team, string.Empty, onlySessionId: null, CancellationToken.None);

        Assert.Equal(SessionStatus.Cancelled, (await dbContext.Sessions.FindAsync(session.Id))!.Status);
    }

    /// <summary>
    /// The other half, and the destructive one. A session-scoped refresh must call
    /// RefreshSessionCandidatesAsync, NOT the team-wide RunAsync — a single-session view of the feed
    /// looks exactly like every other session having been cancelled. If the branch in TeamPipeline is
    /// ever "simplified" into passing the id straight through, this test is what fails.
    /// </summary>
    [Fact]
    public async Task SessionScopedRun_DoesNotCancelSessionsMissingFromTheFeed()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);

        await CreatePipeline(dbContext).RunAsync(team, "Manual", onlySessionId: session.Id, CancellationToken.None);

        Assert.Equal(SessionStatus.Active, (await dbContext.Sessions.FindAsync(session.Id))!.Status);
    }

    [Fact]
    public async Task SessionScopedRun_StillRunsEveryStep()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);

        await CreatePipeline(dbContext).RunAsync(team, "Manual", session.Id, CancellationToken.None);

        var expected = ExpectedSteps.Select(s => "Manual" + s).ToArray();
        var actual = await StepNamesAsync(dbContext);
        Assert.Equal(expected, actual);
    }

    // ---- #242: a step that throws must be counted, not swallowed into a clean-looking result ----

    /// <summary>
    /// Fails every call. Stands in for the real cause of the finding: a team whose ExamTools
    /// credentials are wrong, where every step that touches the feed throws.
    /// </summary>
    private sealed class ThrowingExamToolsClient : IExamToolsClient
    {
        private static Exception Boom() => new InvalidOperationException("ExamTools login failed.");

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken) =>
            throw Boom();

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken) =>
            throw Boom();

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            throw Boom();

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            throw Boom();

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
            throw Boom();
    }

    /// <summary>
    /// The finding itself. JobRunHistoryLogger catches and does not rethrow — correctly, since that
    /// is what stops one team's bad row taking down the Worker — so before this the pipeline could
    /// not tell a caller that anything had gone wrong at all.
    /// </summary>
    [Fact]
    public async Task AFailingStepIsCountedInTheResult()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreatePipeline(dbContext, new ThrowingExamToolsClient())
            .RunAsync(team, jobNamePrefix: "Manual", onlySessionId: null, CancellationToken.None);

        Assert.True(result.FailedSteps > 0,
            "A pipeline whose ExamTools calls all threw must report failed steps — otherwise its zero " +
            "counts are indistinguishable from a run that had nothing to do.");
    }

    /// <summary>
    /// And the pipeline still completes rather than propagating: the swallow-and-continue behavior
    /// is deliberate and must survive this change. A later step failing must not stop earlier ones
    /// from having run, and the run must still be recorded.
    /// </summary>
    [Fact]
    public async Task AFailingStepDoesNotAbortTheRest()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await CreatePipeline(dbContext, new ThrowingExamToolsClient())
            .RunAsync(team, jobNamePrefix: "Manual", onlySessionId: null, CancellationToken.None);

        // Every step still got its history row, failures included — that is the ops dashboard's
        // whole purpose, and it is how the user is told which step broke.
        var steps = await StepNamesAsync(dbContext);
        Assert.Equal(ExpectedSteps.Select(s => "Manual" + s).ToArray(), steps);
    }

    /// <summary>A clean run reports zero, or the failure count is useless.</summary>
    [Fact]
    public async Task ACleanRunReportsNoFailedSteps()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var result = await CreatePipeline(dbContext)
            .RunAsync(team, jobNamePrefix: string.Empty, onlySessionId: null, CancellationToken.None);

        Assert.Equal(0, result.FailedSteps);
    }
}
