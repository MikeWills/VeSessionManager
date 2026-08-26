using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Filing a session with ARRL-VEC (issue #197).
///
/// <para><b>Nothing here reaches ARRL.</b> The client is pointed at a fake handler; the real endpoint
/// is blank outside production and <c>ArrlEndpointIsNotHardcodedTests</c> fails the build if it
/// appears in any source file or test. That is the arrangement, not a promise — there is no sandbox,
/// so a single careless test would file a fabricated session with a real VEC on every CI run.</para>
///
/// <para>The guards are the subject here rather than the happy path: this action cannot be undone,
/// and ARRL has no rollback beyond a phone call.</para>
/// </summary>
public class ArrlSubmissionServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionStartUtc = new(2026, 4, 22, 1, 30, 0, DateTimeKind.Utc);
    private const string ArchiveName = "ExamSession_MARC_20260422_0130_arrl.zip";

    private readonly string root = Path.Combine(Path.GetTempPath(), "vesm-arrlsvc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>Answers whatever the test queues, and records that it was asked at all.</summary>
    private sealed class FakeArrlHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Reply { get; set; } = () => Receipt(ArchiveName);
        public Exception? Throw { get; set; }
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts++;
            return Throw is not null ? Task.FromException<HttpResponseMessage>(Throw) : Task.FromResult(Reply());
        }
    }

    private static HttpResponseMessage Receipt(params string[] confirmedFileNames)
    {
        var body = string.Join("", confirmedFileNames.Select(n => $"<p><b>{n}</b> has been uploaded successfully.</p>"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private sealed class World
    {
        public required AppDbContext Db { get; init; }
        public required FakeArrlHandler Handler { get; init; }
        public required ArrlSubmissionService Service { get; init; }
        public required Session Session { get; init; }
        public required User User { get; init; }
    }

    private World Build(
        string? uploadUrl = "https://example.invalid/upload",
        Action<Session>? configureSession = null,
        Action<Team>? configureTeam = null,
        string globalExamToolsBaseUrl = "https://exam.tools")
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var team = new Team { Name = "MARC", CreatedUtc = Now, ExamToolsTeamCode = "MARC" };
        configureTeam?.Invoke(team);
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Session Manager", Role = UserRole.SessionManager };
        var fee = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1), FeeCollectionEnabled = true,
            ExamFeeAmount = 15m, RetainedAmount = 7m, CreatedUtc = Now, CreatedByUser = user
        };
        var session = new Session
        {
            ExamToolsSessionId = "6950a2cbf593f706d2e92247", Title = "Testing", Team = team, Vec = vec,
            FeeConfiguration = fee, ScheduledStartUtc = SessionStartUtc, DurationMinutes = 120, CreatedUtc = Now
        };
        configureSession?.Invoke(session);
        db.AddRange(team, vec, user, fee, session);
        db.SaveChanges();

        var handler = new FakeArrlHandler();
        var options = Options.Create(new ArrlSubmissionOptions { UploadUrl = uploadUrl, ArchiveRootPath = root });
        var client = new ArrlSubmissionClient(new HttpClient(handler), options, NullLogger<ArrlSubmissionClient>.Instance);
        var store = new ArrlSubmissionArchiveStore(options, NullLogger<ArrlSubmissionArchiveStore>.Instance);

        var examToolsOptions = Options.Create(new ExamToolsOptions { BaseUrl = globalExamToolsBaseUrl });

        return new World
        {
            Db = db, Handler = handler, Session = session, User = user,
            Service = new ArrlSubmissionService(db, client, store, examToolsOptions, new FixedTimeProvider(Now), NullLogger<ArrlSubmissionService>.Instance)
        };
    }

    private static ArrlSubmissionFieldValues Fields() => new()
    {
        FullName = "Mike Wills", CallSign = "WX0MIK", Email = "wx0mik@gmail.com", Phone = "5073814969",
        SessionDate = "2026-04-21", Location = "Remote Online",
        PaymentMethod = ArrlPaymentMethod.CreditCardOnFile, AmountCharged = "8.00"
    };

    private static ArrlSubmissionFile Archive() => new(ArchiveName, [0x50, 0x4B, 0x03, 0x04]);

    private static Task<ArrlSubmitResult> SubmitAsync(World world, ArrlSubmissionFile? attachment = null) =>
        world.Service.SubmitAsync(world.Session.Id, Fields(), Archive(), attachment, world.User.Id, CancellationToken.None);

    // ---- The happy path ----------------------------------------------------------------------

    [Fact]
    public async Task AConfirmedReceipt_MarksTheSessionSubmittedAndRecordsWhatWasSent()
    {
        var world = Build();

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.Succeeded, result);

        var session = await world.Db.Sessions.SingleAsync();
        Assert.Equal(VecSubmissionStatus.Submitted, session.VecSubmissionStatus);
        Assert.Equal(world.User.Id, session.VecSubmittedByUserId);

        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.Equal(ArrlReceiptOutcome.Succeeded, submission.Outcome);
        Assert.Equal("Mike Wills", submission.FullName);
        Assert.Equal("8.00", submission.AmountCharged);
        Assert.Contains("uploaded successfully", submission.ResponseBody);
    }

    /// <summary>The evidence is what the team goes back to, so it is written to disk as well as recorded.</summary>
    [Fact]
    public async Task TheFilesAreKeptUnderTeamVecYearMonth()
    {
        var world = Build();

        await SubmitAsync(world);

        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.Equal(Path.Combine("MARC", "arrl", "2026", "04", ArchiveName), submission.ArchiveStoredPath);
        Assert.True(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
    }

    [Fact]
    public async Task BothFilesAreSentAndKept()
    {
        var world = Build();
        world.Handler.Reply = () => Receipt(ArchiveName, "youth-grant.pdf");

        var result = await SubmitAsync(world, new ArrlSubmissionFile("youth-grant.pdf", [1, 2, 3]));

        Assert.Equal(ArrlSubmitResult.Succeeded, result);
        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.Equal("youth-grant.pdf", submission.AttachmentFileName);
        Assert.True(File.Exists(Path.Combine(root, submission.AttachmentStoredPath!)));
    }

    // ---- The guards --------------------------------------------------------------------------

    /// <summary>ARRL cannot dedupe and has no unsend, so the one-way toggle is a hard refusal here.</summary>
    [Fact]
    public async Task AnAlreadySubmittedSession_IsRefusedWithoutPosting()
    {
        var world = Build(configureSession: s => s.VecSubmissionStatus = VecSubmissionStatus.Submitted);

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.AlreadySubmitted, result);
        Assert.Equal(0, world.Handler.Posts);
    }

    /// <summary>
    /// The guard that matters most. An earlier attempt whose receipt could not be read <b>may already
    /// have been filed</b> — resending it is precisely the duplicate that cannot be undone, and the
    /// session is still marked unsubmitted, so nothing else would stop a second press.
    /// </summary>
    [Fact]
    public async Task AnEarlierUnconfirmedAttempt_BlocksASecondSubmission()
    {
        var world = Build();
        world.Handler.Reply = () => Receipt("something-else.zip");

        var first = await SubmitAsync(world);
        var second = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.Unconfirmed, first);
        Assert.Equal(ArrlSubmitResult.AlreadyAttempted, second);
        Assert.Equal(1, world.Handler.Posts);
    }

    /// <summary>
    /// Defense in depth against ArrlSubmissionPreviewService's own gate — this is the one place a
    /// submission actually happens, so it must refuse on its own even if a caller somehow bypassed the
    /// preview screen.
    /// </summary>
    [Fact]
    public async Task ATeamOnExamToolsTestSite_IsRefusedWithoutPosting()
    {
        var world = Build(globalExamToolsBaseUrl: "https://examtools.dev");

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.TeamOnTestExamTools, result);
        Assert.Equal(0, world.Handler.Posts);
        Assert.Empty(world.Db.ArrlVecSubmissions);
    }

    [Fact]
    public async Task ATeamsOwnOverrideToTheTestSite_IsAlsoRefused()
    {
        var world = Build(configureTeam: t => t.ExamToolsBaseUrl = "https://examtools.dev");

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.TeamOnTestExamTools, result);
        Assert.Equal(0, world.Handler.Posts);
    }

    [Fact]
    public async Task WithNoUploadUrlConfigured_NothingIsSentAndTheCallerIsTold()
    {
        var world = Build(uploadUrl: null);

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.NotConfigured, result);
        Assert.Equal(0, world.Handler.Posts);
        Assert.Empty(world.Db.ArrlVecSubmissions);
    }

    // ---- Unknown outcomes --------------------------------------------------------------------

    /// <summary>
    /// A receipt that does not confirm what we sent leaves the session <b>unsubmitted on purpose</b>.
    /// Marking it would hide the one case that needs a human.
    /// </summary>
    [Fact]
    public async Task AnUnreadableReceipt_LeavesTheSessionUnsubmittedAndRecordsWhyRatherThanThrowing()
    {
        var world = Build();
        world.Handler.Reply = () => Receipt("a-different-file.zip");

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.Unconfirmed, result);
        Assert.Equal(VecSubmissionStatus.NotSubmitted, (await world.Db.Sessions.SingleAsync()).VecSubmissionStatus);

        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.Equal(ArrlReceiptOutcome.Unknown, submission.Outcome);
        Assert.Contains(ArchiveName, submission.UnconfirmedFileNames);
    }

    /// <summary>
    /// A transport failure is <b>not</b> proof nothing was filed — the request left this machine. The
    /// attempt is recorded rather than thrown away, which is what lets a human resolve it and what
    /// stops a second press from filing twice.
    /// </summary>
    [Fact]
    public async Task ATransportFailure_IsRecordedRatherThanRaised()
    {
        var world = Build();
        world.Handler.Throw = new HttpRequestException("connection reset");

        var result = await SubmitAsync(world);

        Assert.Equal(ArrlSubmitResult.Unconfirmed, result);
        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.Contains("connection reset", submission.TransportError);
        Assert.Equal(VecSubmissionStatus.NotSubmitted, (await world.Db.Sessions.SingleAsync()).VecSubmissionStatus);
    }

    /// <summary>
    /// Written before the POST, so a request that never returns still leaves evidence of what went.
    /// Storing afterwards would lose exactly the case the archive exists for.
    /// </summary>
    [Fact]
    public async Task TheEvidenceSurvivesARequestThatNeverCompletes()
    {
        var world = Build();
        world.Handler.Throw = new HttpRequestException("connection reset");

        await SubmitAsync(world);

        var submission = await world.Db.ArrlVecSubmissions.SingleAsync();
        Assert.NotNull(submission.ArchiveStoredPath);
        Assert.True(File.Exists(Path.Combine(root, submission.ArchiveStoredPath!)));
    }

    [Fact]
    public async Task AnUnknownSession_IsNotFound()
    {
        var world = Build();

        var result = await world.Service.SubmitAsync(
            999999, Fields(), Archive(), null, world.User.Id, CancellationToken.None);

        Assert.Equal(ArrlSubmitResult.SessionNotFound, result);
        Assert.Equal(0, world.Handler.Posts);
    }

    /// <summary>Both outcomes are audited, and the unconfirmed one says what to do rather than reading as a failure.</summary>
    [Fact]
    public async Task BothOutcomesAreAudited()
    {
        var confirmed = Build();
        await SubmitAsync(confirmed);
        Assert.Contains(confirmed.Db.AuditLogs, a => a.Action == "ArrlSubmissionFiled");

        var unconfirmed = Build();
        unconfirmed.Handler.Reply = () => Receipt("other.zip");
        await SubmitAsync(unconfirmed);
        Assert.Contains(unconfirmed.Db.AuditLogs, a => a.Action == "ArrlSubmissionUnconfirmed");
    }
}
