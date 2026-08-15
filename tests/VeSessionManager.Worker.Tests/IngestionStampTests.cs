using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.VolunteerExaminers;
using VeSessionManager.Core.Zoom;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// The ingestion throttle stamp: <c>Team.LastIngestionRunUtc</c> must advance on every tick that
/// actually polls a team, because it is the only thing stopping that team being re-polled on every
/// 300-second tick instead of every <c>SessionIngestionIntervalMinutes</c>.
///
/// <para><b>Read this before trusting issue #232's description.</b> That finding said a failed
/// pipeline step makes <c>JobRunHistoryLogger</c> call <c>ChangeTracker.Clear()</c>, detaching the
/// team, so the old <c>team.LastIngestionRunUtc = …; SaveChangesAsync()</c> silently wrote nothing —
/// and that this was live in production. **These tests do not reproduce that**, and were run against
/// the pre-fix tracked-write implementation to check: it passes there too.</para>
///
/// <para>What was confirmed, by isolating each step: the <i>mechanism</i> is real — after a manual
/// <c>ChangeTracker.Clear()</c>, the assignment is lost and <c>SaveChangesAsync</c> returns 0 with the
/// old value left in the database. But <c>JobRunHistoryLogger.RunAsync</c> with a throwing action
/// leaves the team <b>still tracked</b>, so the trigger never fires. Why the clear in
/// <c>TryCompleteHistoryAsync</c> does not reach it is left as an open question on #232 rather than
/// guessed at here.</para>
///
/// <para>So these are <b>characterization tests</b>, not a regression guard for #232: they pin that
/// the stamp advances whether the pipeline succeeds or fails, and that a team which is not due is
/// neither polled nor stamped. The shipped <c>ExecuteUpdateAsync</c> is still the better shape — it
/// cannot be undone by the tracker, whatever the tracker does — but it fixed a bug nobody has
/// demonstrated.</para>
///
/// <para>The real pipeline is wired rather than stubbed, with a failing ExamTools client standing in
/// for expired credentials. A fake pipeline would model away the interaction these tests exist to
/// observe.</para>
/// </summary>
public class IngestionStampTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Fails every call, the way expired or revoked ExamTools credentials do.</summary>
    private sealed class FailingExamToolsClient : IExamToolsClient
    {
        public int Calls { get; private set; }

        private Task<T> Fail<T>()
        {
            Calls++;
            return Task.FromException<T>(new HttpRequestException("ExamTools said no"));
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct) => Fail<IReadOnlyList<ExamToolsSession>>();
        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials c, DateOnly s, DateOnly e, CancellationToken ct) => Fail<IReadOnlyList<ExamToolsSession>>();
        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) => Fail<IReadOnlyList<ExamToolsApplicant>>();
        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) => Fail<IReadOnlyList<ExamToolsVe>>();
        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) => Fail<ExamToolsApplicantDetail?>();
    }

    /// <summary>
    /// Succeeds at everything, for the control case. Only the ExamTools steps are reached anyway —
    /// the team below configures no other integration, so Zoom/Discord/Square/Email skip quietly by
    /// the optional-integration rule.
    /// </summary>
    private sealed class WorkingExamToolsClient : IExamToolsClient
    {
        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);
        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials c, DateOnly s, DateOnly e, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);
        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExamToolsApplicant>>([]);
        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);
        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) => Task.FromResult<ExamToolsApplicantDetail?>(null);
    }

    /// <summary>
    /// Never called — the team has no Zoom/Discord/Square/SMTP credentials, so every consumer skips
    /// before reaching the client. Throwing rather than returning empty makes that assumption fail
    /// loudly if it stops holding, instead of quietly changing what the test covers.
    /// </summary>
    private sealed class UnreachableClients : IZoomClient, IDiscordEventClient, ISquareClient, IEmailSender
    {
        private static Task<T> Nope<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
            throw new InvalidOperationException($"{member} should be unreachable — the team configures no such integration.");

        public bool IsConfigured => false;

        public Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials c, ZoomMeetingRequest r, CancellationToken ct) => Nope<ZoomMeeting>();
        public Task UpdateMeetingAsync(ZoomCredentials c, string id, ZoomMeetingRequest r, CancellationToken ct) => Nope<object>();
        public Task DeleteMeetingAsync(ZoomCredentials c, string id, CancellationToken ct) => Nope<object>();
        public Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials c, CancellationToken ct) => Nope<IReadOnlyList<ZoomMeeting>>();

        public Task<DiscordEvent> CreateEventAsync(ulong g, DiscordEventRequest r, CancellationToken ct) => Nope<DiscordEvent>();
        public Task UpdateEventAsync(ulong g, string id, DiscordEventRequest r, CancellationToken ct) => Nope<object>();
        public Task DeleteEventAsync(ulong g, string id, CancellationToken ct) => Nope<object>();
        public Task<IReadOnlyList<DiscordEvent>> ListEventsAsync(ulong g, CancellationToken ct) => Nope<IReadOnlyList<DiscordEvent>>();

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials c, SquarePaymentLinkRequest r, CancellationToken ct) => Nope<SquarePaymentLink>();
        public Task CompleteOrderAsync(SquareCredentials c, string o, CancellationToken ct) => Nope<object>();
        public Task DeletePaymentLinkAsync(SquareCredentials c, string l, CancellationToken ct) => Nope<object>();
        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials c, SquareRefundRequest r, CancellationToken ct) => Nope<SquareRefund>();
        public Task<SquareRefund> GetRefundAsync(SquareCredentials c, string id, CancellationToken ct) => Nope<SquareRefund>();

        public Task SendAsync(EmailCredentials c, EmailMessage m, CancellationToken ct) => Nope<object>();
    }

    private static async Task<WorkerTickHarness> CreateHarnessAsync(IExamToolsClient examTools)
    {
        var unreachable = new UnreachableClients();

        return await WorkerTickHarness.CreateAsync(services =>
        {
            // ONE provider for every scope. AppDbContext's convenience constructor makes its own
            // EphemeralDataProtectionProvider, so without this each scoped context would hold a
            // different key and the team's encrypted credentials would not survive the round trip.
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            services.AddSingleton(new FixedTimeProvider(Now));
            services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedTimeProvider>());
            services.AddSingleton(Options.Create(new ExamToolsOptions { BaseUrl = "https://exam.tools" }));
            services.AddSingleton(Options.Create(new AppOptions { PublicBaseUrl = "https://localhost" }));

            services.AddSingleton(examTools);
            services.AddSingleton<IZoomClient>(unreachable);
            services.AddSingleton<IDiscordEventClient>(unreachable);
            services.AddSingleton<ISquareClient>(unreachable);
            services.AddSingleton<IEmailSender>(unreachable);

            // The real pipeline and every step it runs. A stub here would model away the bug.
            services.AddScoped<SessionIngestionService>();
            services.AddScoped<VolunteerExaminerSyncService>();
            services.AddScoped<ExamResultSyncService>();
            // Singleton, matching both hosts: TeamIntegrationState's whole job is remembering
            // across the per-tick scopes, so a scoped registration here would quietly diverge from
            // production (#64).
            services.AddSingleton<TeamIntegrationState>();
            services.AddScoped<SessionEventSchedulingService>();
            services.AddScoped<PaymentGenerationService>();
            services.AddScoped<EmailTemplateRenderer>();
            services.AddScoped<CandidateNotificationService>();
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<TeamPipeline>();
            services.AddScoped<IngestionScheduleService>();
            services.AddScoped<SystemSettingsService>();
        });
    }

    private static async Task<Team> SeedConfiguredTeamAsync(WorkerTickHarness harness, string name)
    {
        await using var dbContext = harness.NewContext();
        var team = new Team
        {
            Name = name,
            ExamToolsTeamCode = name,
            ExamToolsUsername = "ve@example.org",
            ExamToolsPassword = "secret",
            CreatedUtc = Now.AddYears(-1),
            // Long enough ago that IsDue is unambiguously true.
            LastIngestionRunUtc = Now.AddDays(-1)
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>
    /// Every pipeline step fails, and the throttle stamp must still reach the database — it is what
    /// stops the team being re-polled every 300 seconds forever. Passes against the pre-fix tracked
    /// write too; see the class remarks on why this is characterization rather than a #232 guard.
    /// </summary>
    [Fact]
    public async Task WhenEveryPipelineStepFails_TheThrottleStampStillAdvances()
    {
        var examTools = new FailingExamToolsClient();
        await using var harness = await CreateHarnessAsync(examTools);
        var team = await SeedConfiguredTeamAsync(harness, "HRCC");

        var job = new SessionIngestionJob(harness.ScopeFactory, harness.Configuration, Quiet.Logger<SessionIngestionJob>());
        await job.RunTickAsync(CancellationToken.None);

        // Precondition, not decoration: if the client was never called the pipeline never ran, and
        // the assertion below would pass for the wrong reason.
        Assert.True(examTools.Calls > 0, "the pipeline never reached ExamTools, so nothing failed");

        await using var verify = harness.NewContext();
        var reloaded = await verify.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);

        Assert.Equal(Now, reloaded.LastIngestionRunUtc);
    }

    /// <summary>
    /// The failures are recorded rather than swallowed — the stamp advancing must not be mistaken for
    /// the run having gone well.
    /// </summary>
    [Fact]
    public async Task TheFailedStepsAreStillRecordedAgainstTheTeam()
    {
        await using var harness = await CreateHarnessAsync(new FailingExamToolsClient());
        var team = await SeedConfiguredTeamAsync(harness, "HRCC");

        var job = new SessionIngestionJob(harness.ScopeFactory, harness.Configuration, Quiet.Logger<SessionIngestionJob>());
        await job.RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var history = await verify.JobRunHistories.AsNoTracking().Where(h => h.TeamId == team.Id).ToListAsync();

        Assert.NotEmpty(history);
        Assert.Contains(history, h => !h.Success);
    }

    /// <summary>Control: the stamp advances on a clean run too, so the test above is not passing on an unrelated path.</summary>
    [Fact]
    public async Task WhenThePipelineSucceeds_TheThrottleStampAlsoAdvances()
    {
        await using var harness = await CreateHarnessAsync(new WorkingExamToolsClient());
        var team = await SeedConfiguredTeamAsync(harness, "HRCC");

        var job = new SessionIngestionJob(harness.ScopeFactory, harness.Configuration, Quiet.Logger<SessionIngestionJob>());
        await job.RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var reloaded = await verify.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);

        Assert.Equal(Now, reloaded.LastIngestionRunUtc);
        Assert.DoesNotContain(await verify.JobRunHistories.AsNoTracking().ToListAsync(), h => !h.Success);
    }

    /// <summary>
    /// A team not yet due must not be stamped — otherwise the fix would trade one throttle bug for
    /// its mirror image, resetting the clock on every tick and never polling at all.
    /// </summary>
    [Fact]
    public async Task ATeamThatIsNotDue_IsNeitherPolledNorStamped()
    {
        var examTools = new FailingExamToolsClient();
        await using var harness = await CreateHarnessAsync(examTools);

        await using (var seed = harness.NewContext())
        {
            seed.Teams.Add(new Team
            {
                Name = "JustRan",
                ExamToolsTeamCode = "JustRan",
                ExamToolsUsername = "ve@example.org",
                ExamToolsPassword = "secret",
                CreatedUtc = Now.AddYears(-1),
                LastIngestionRunUtc = Now.AddSeconds(-30)   // well inside any sane interval
            });
            await seed.SaveChangesAsync();
        }

        var job = new SessionIngestionJob(harness.ScopeFactory, harness.Configuration, Quiet.Logger<SessionIngestionJob>());
        await job.RunTickAsync(CancellationToken.None);

        Assert.Equal(0, examTools.Calls);

        await using var verify = harness.NewContext();
        var reloaded = await verify.Teams.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddSeconds(-30), reloaded.LastIngestionRunUtc);
    }
}
