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
/// Building the ARRL submission preview (issue #197).
///
/// <para><b>This carries most of the coverage for the whole feature, deliberately.</b> There is no
/// sandbox on ARRL's side and no dry-run: the POST itself can only ever be exercised by filing a
/// real session with a real VEC. The preview is the part that *can* be tested, so what it resolves
/// has to be pinned here.</para>
/// </summary>
public class ArrlSubmissionPreviewServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The real MARC session behind the receipt on #197: 01:30 UTC on the 22nd is the evening of the 21st in Eastern.</summary>
    private static readonly DateTime SessionStartUtc = new(2026, 4, 22, 1, 30, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeArchiveClient : IExamToolsClient
    {
        public VecArchiveDownload Result { get; set; } =
            VecArchiveDownload.Succeeded([1, 2, 3, 4], "ExamSession_MARC_20260422_0130_arrl.zip");

        public string? RequestedVecCode { get; private set; }
        public string? RequestedSessionId { get; private set; }
        public int Calls { get; private set; }

        public Task<VecArchiveDownload> DownloadVecArchiveAsync(ExamToolsCredentials credentials, string examToolsSessionId, string vecCode, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedSessionId = examToolsSessionId;
            RequestedVecCode = vecCode;
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials c, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials c, DateOnly s, DateOnly e, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials c, string s, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials c, string s, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials c, string s, string a, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class World
    {
        public required AppDbContext Db { get; init; }
        public required FakeArchiveClient Client { get; init; }
        public required Session Session { get; init; }
        public required Team Team { get; init; }

        public ArrlSubmissionPreviewService Service => new(
            Db, Client, Options.Create(new ExamToolsOptions { BaseUrl = "https://exam.tools" }),
            NullLogger<ArrlSubmissionPreviewService>.Instance);

        public Task<ArrlSubmissionPreview> BuildAsync() =>
            Service.BuildAsync(Session.Id, CancellationToken.None);
    }

    private static async Task<World> SeedAsync(
        string vecName = "ARRL",
        Action<Team>? configureTeam = null,
        Action<Session>? configureSession = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var team = new Team
        {
            Name = "MARC",
            CreatedUtc = Now,
            ExamToolsTeamCode = "MARC",
            ExamToolsUsername = "ve@example.org",
            ExamToolsPassword = "pw",
            ArrlSubmissionNamePostfix = null,
            ArrlSubmissionEmailSource = ArrlSubmissionEmailSource.SessionLead,
            ArrlSubmissionLocation = "Remote Online",
            ArrlSubmissionPaymentMethod = ArrlPaymentMethod.CreditCardOnFile
        };
        configureTeam?.Invoke(team);

        var vec = new Vec { Name = vecName };
        var fee = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1), FeeCollectionEnabled = true,
            ExamFeeAmount = 15m, RetainedAmount = 7m, YouthExamFeeAmount = 5m, CreatedUtc = Now,
            CreatedByUser = new User { Name = "Seed", Role = UserRole.SystemAdmin }
        };

        var lead = new VolunteerExaminer
        {
            Name = "Mike Wills", CallSign = "WX0MIK", Email = "wx0mik@gmail.com",
            Phone = "5073814969", CreatedUtc = Now
        };

        var session = new Session
        {
            ExamToolsSessionId = "6950a2cbf593f706d2e92247",
            Title = "Testing for Minnesotans",
            Team = team, Vec = vec, FeeConfiguration = fee,
            TeamLeadCallSign = "WX0MIK",
            ScheduledStartUtc = SessionStartUtc,
            DurationMinutes = 120,
            ExamToolsClosedUtc = Now,
            CreatedUtc = Now
        };
        configureSession?.Invoke(session);

        db.AddRange(team, vec, fee, lead, session);
        await db.SaveChangesAsync();

        return new World { Db = db, Client = new FakeArchiveClient(), Session = session, Team = team };
    }

    private static void AddPaidCandidate(World world, decimal amount, Action<Payment>? configure = null)
    {
        var candidate = new Candidate
        {
            Session = world.Session, ExamToolsApplicantId = Guid.NewGuid().ToString(),
            Name = "Test Candidate", FirstName = "Test", DateRegisteredUtc = Now
        };
        var payment = new Payment { Candidate = candidate, Amount = amount, Status = PaymentStatus.Paid };
        configure?.Invoke(payment);
        world.Db.AddRange(candidate, payment);
        world.Db.SaveChanges();
    }

    // ---- The happy path ---------------------------------------------------------------------

    [Fact]
    public async Task EveryFieldResolvesFromTheTeamAndTheSessionLead()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m);

        var preview = await world.BuildAsync();

        Assert.Equal(ArrlSubmissionPreviewStatus.Ready, preview.Status);
        Assert.Equal("Mike Wills", preview.FullName);
        Assert.Equal("WX0MIK", preview.CallSign);
        Assert.Equal("wx0mik@gmail.com", preview.Email);
        Assert.Equal("5073814969", preview.Phone);
        Assert.Equal("Remote Online", preview.Location);
        Assert.Equal(ArrlPaymentMethod.CreditCardOnFile, preview.PaymentMethod);
        Assert.Empty(preview.MissingRequiredFields);
        Assert.True(preview.CanSubmit);
    }

    /// <summary>
    /// The session ran on the evening of 21 April Eastern and starts at 01:30 UTC on the 22nd. Using
    /// <c>.Date</c> would file the 22nd — the #248 bug class, which is wrong for ~80% of this
    /// deployment's sessions. The real receipt says <c>2026-04-21</c>.
    /// </summary>
    [Fact]
    public async Task TheSessionDateIsTheEasternCalendarDate_NotTheUtcOne()
    {
        var world = await SeedAsync();

        var preview = await world.BuildAsync();

        Assert.Equal("2026-04-21", preview.SessionDate);
    }

    /// <summary>HRCC's real value opens with a slash and no space, so nothing may be inserted between the two.</summary>
    [Fact]
    public async Task ThePostfixIsAppendedVerbatim_WithNoSeparator()
    {
        var world = await SeedAsync(configureTeam: t => t.ArrlSubmissionNamePostfix = "/Nick Booth (CC)/HRCC VE Team");

        var preview = await world.BuildAsync();

        Assert.Equal("Mike Wills/Nick Booth (CC)/HRCC VE Team", preview.FullName);
    }

    [Fact]
    public async Task ATeamAddressOverridesTheLeadsEmail()
    {
        var world = await SeedAsync(configureTeam: t =>
        {
            t.ArrlSubmissionEmailSource = ArrlSubmissionEmailSource.TeamAddress;
            t.ArrlSubmissionEmail = "vec@marcradio.org";
        });

        var preview = await world.BuildAsync();

        Assert.Equal("vec@marcradio.org", preview.Email);
    }

    // ---- The amount -------------------------------------------------------------------------

    /// <summary>
    /// Two candidates at $15 with $7 retained each is $16 remitted — which is exactly what HRCC's real
    /// receipt shows for a two-candidate session.
    /// </summary>
    [Fact]
    public async Task TheAmountIsTheRemitToVecTotal_FormattedWithoutADollarSign()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m);
        AddPaidCandidate(world, 15m);

        var preview = await world.BuildAsync();

        Assert.Equal("16.00", preview.AmountCharged);
        Assert.Equal(30m, preview.Fees!.TotalCollected);
        Assert.Equal(16m, preview.Fees.TotalRemitToVec);
    }

    /// <summary>
    /// A refund does not move a payment off Paid (#375, deliberately — otherwise the "unpaid and no
    /// link" scan would issue a fresh checkout link), so the total still counts it. Surfaced rather
    /// than silently corrected: only a human knows whether the candidate was filed.
    /// </summary>
    [Fact]
    public async Task ARefundedPaymentIsFlagged()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m, p => p.Refunds.Add(new Refund
        {
            Team = world.Team, AmountUsd = 15m, Status = RefundStatus.Completed,
            RequestedUtc = Now, SquarePaymentId = "sq-pay-1", SquareIdempotencyKey = "idem-1", SquareRefundId = "r1"
        }));

        var preview = await world.BuildAsync();

        Assert.Contains(preview.AmountWarnings, w => w.Contains("refund", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The out-of-band youth path: Square reports $5 while Amount stays $15, so the remit is computed
    /// on money that never arrived.
    /// </summary>
    [Fact]
    public async Task AnAmountMismatchIsFlagged()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m, p =>
        {
            p.SquareAmountPaidUsd = 5m;
            p.AmountMismatchFlaggedUtc = Now;
        });

        var preview = await world.BuildAsync();

        Assert.Contains(preview.AmountWarnings, w => w.Contains("differ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnOrdinarySessionHasNoAmountWarnings()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m);

        var preview = await world.BuildAsync();

        Assert.Empty(preview.AmountWarnings);
    }

    /// <summary>A youth-rate payment is when ARRL also wants the grant program form — the second of the two files.</summary>
    [Fact]
    public async Task AYouthRatePaymentExpectsTheGrantForm()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 5m);

        var preview = await world.BuildAsync();

        Assert.True(preview.YouthFormExpected);
    }

    [Fact]
    public async Task AStandardRateSessionDoesNotExpectTheGrantForm()
    {
        var world = await SeedAsync();
        AddPaidCandidate(world, 15m);

        var preview = await world.BuildAsync();

        Assert.False(preview.YouthFormExpected);
    }

    // ---- Missing required values -------------------------------------------------------------

    /// <summary>
    /// ExamTools supplies no contact details at all and the VE retention purge clears them, so a lead
    /// with no phone on record is a real, ordinary state — named individually so the operator knows
    /// which box to fill rather than being told "something is missing".
    /// </summary>
    [Fact]
    public async Task ALeadWithNoPhone_IsNamedAndBlocksSubmission()
    {
        var world = await SeedAsync();
        var lead = await world.Db.VolunteerExaminers.SingleAsync();
        lead.Phone = null;
        await world.Db.SaveChangesAsync();

        var preview = await world.BuildAsync();

        Assert.Contains(preview.MissingRequiredFields, f => f.Contains("phone", StringComparison.OrdinalIgnoreCase));
        Assert.False(preview.CanSubmit);
    }

    /// <summary>A session whose lead call sign matches no VE record leaves four fields empty at once, and must say so rather than rendering a form of blanks.</summary>
    [Fact]
    public async Task AnUnresolvableLead_NamesEveryFieldItWouldHaveFilled()
    {
        var world = await SeedAsync(configureSession: s => s.TeamLeadCallSign = "N0BODY");

        var preview = await world.BuildAsync();

        Assert.Contains(preview.MissingRequiredFields, f => f.Contains("name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.MissingRequiredFields, f => f.Contains("call sign", StringComparison.OrdinalIgnoreCase));
        Assert.False(preview.CanSubmit);
    }

    // ---- Gates ------------------------------------------------------------------------------

    /// <summary>
    /// One submitter, no default (#197's first constraint). A session under another VEC finds nothing
    /// rather than being handed ARRL's.
    /// </summary>
    [Fact]
    public async Task ANonArrlSession_IsRefusedRatherThanHandedTheArrlSubmitter()
    {
        var world = await SeedAsync(vecName: "GLAARG");

        var preview = await world.BuildAsync();

        Assert.Equal(ArrlSubmissionPreviewStatus.NotAnArrlSession, preview.Status);
        Assert.False(preview.CanSubmit);
        Assert.Equal(0, world.Client.Calls);
    }

    [Fact]
    public async Task AnUnconfiguredTeam_IsToldSoBeforeAnythingIsFetched()
    {
        var world = await SeedAsync(configureTeam: t => t.ArrlSubmissionLocation = null);

        var preview = await world.BuildAsync();

        Assert.Equal(ArrlSubmissionPreviewStatus.TeamNotConfigured, preview.Status);
        Assert.Equal(0, world.Client.Calls);
    }

    [Fact]
    public async Task AnAlreadySubmittedSession_CannotBeSubmittedAgain()
    {
        var world = await SeedAsync(configureSession: s => s.VecSubmissionStatus = VecSubmissionStatus.Submitted);

        var preview = await world.BuildAsync();

        Assert.True(preview.AlreadySubmitted);
        Assert.False(preview.CanSubmit);
    }

    // ---- The archive ------------------------------------------------------------------------

    [Fact]
    public async Task TheArchiveIsFetchedForThisSessionAndVec()
    {
        var world = await SeedAsync();

        var preview = await world.BuildAsync();

        Assert.Equal("6950a2cbf593f706d2e92247", world.Client.RequestedSessionId);
        // Case-insensitive on purpose: this asserts the service hands over the session's own VEC code,
        // not that it pre-lowercases it. Lower-casing is the client's documented job and is pinned by
        // VecArchiveDownloadTests — asserting it here too would fail the day MatchCode's stored casing
        // changes, for a behaviour that is still correct.
        Assert.Equal("arrl", world.Client.RequestedVecCode, ignoreCase: true);
        Assert.Equal("ExamSession_MARC_20260422_0130_arrl.zip", preview.ArchiveFileName);
        Assert.Equal(4, preview.ArchiveByteCount);
    }

    /// <summary>The commonest expected failure, and self-correcting — so ExamTools' own wording is carried through.</summary>
    [Fact]
    public async Task AnUnfinishedSession_ShowsExamToolsOwnWordingAndBlocksSubmission()
    {
        var world = await SeedAsync();
        world.Client.Result = VecArchiveDownload.SessionNotComplete("Exam Session needs to be completed");

        var preview = await world.BuildAsync();

        Assert.Equal(VecArchiveDownloadOutcome.SessionNotComplete, preview.ArchiveOutcome);
        Assert.Equal("Exam Session needs to be completed", preview.ArchiveMessage);
        Assert.False(preview.CanSubmit);
    }

    /// <summary>
    /// The descriptive filename normally arrives in Content-Disposition. When it does not, the app
    /// rebuilds it rather than falling back to the URL's generic name — which is identical for every
    /// session of every team.
    /// </summary>
    [Fact]
    public async Task WithNoFilenameFromExamTools_TheDescriptiveOneIsRebuilt()
    {
        var world = await SeedAsync();
        world.Client.Result = VecArchiveDownload.Succeeded([1, 2, 3, 4], fileName: null);

        var preview = await world.BuildAsync();

        Assert.Equal("ExamSession_MARC_20260422_0130_arrl.zip", preview.ArchiveFileName);
    }

    [Fact]
    public async Task AnUnknownSession_IsNotFound()
    {
        var world = await SeedAsync();

        var preview = await world.Service.BuildAsync(999999, CancellationToken.None);

        Assert.Equal(ArrlSubmissionPreviewStatus.SessionNotFound, preview.Status);
    }
}
