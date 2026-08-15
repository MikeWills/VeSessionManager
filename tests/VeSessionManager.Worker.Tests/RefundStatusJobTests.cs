using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <see cref="RefundStatusJob"/> — the tick that follows a submitted Square refund to a conclusion
/// (#375).
///
/// <para>This job exists because a refund is the one outbound call in this app that is not finished
/// when it returns: Square answers PENDING and can take up to 14 days, and can still end REJECTED.
/// So the cases that matter are the ones where nothing looks wrong — a pending refund that must
/// keep being asked about, and a refund whose original call never came back, which nothing else
/// would ever complete because the user saw an error and has no reason to click again.</para>
/// </summary>
public class RefundStatusJobTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Answers whatever the test sets, and records what it was asked — the two halves this job's behavior is asserted on.</summary>
    private sealed class ScriptedSquareClient : ISquareClient
    {
        public List<string> StatusChecks { get; } = [];
        public List<SquareRefundRequest> RefundRequests { get; } = [];
        public string StatusToReturn { get; set; } = "COMPLETED";
        public Exception? ThrowOnGet { get; set; }

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken)
        {
            StatusChecks.Add(squareRefundId);
            if (ThrowOnGet is not null)
            {
                throw ThrowOnGet;
            }

            return Task.FromResult(new SquareRefund { Id = squareRefundId, Status = StatusToReturn, AmountUsd = 15m });
        }

        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken)
        {
            RefundRequests.Add(request);
            return Task.FromResult(new SquareRefund
            {
                Id = $"refund-for-{request.IdempotencyKey}",
                Status = StatusToReturn,
                AmountUsd = request.AmountUsd
            });
        }

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials c, SquarePaymentLinkRequest r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task CompleteOrderAsync(SquareCredentials c, string orderId, CancellationToken ct) => Task.CompletedTask;
        public Task DeletePaymentLinkAsync(SquareCredentials c, string linkId, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<(WorkerTickHarness Harness, ScriptedSquareClient Square)> CreateHarnessAsync()
    {
        var square = new ScriptedSquareClient();
        var harness = await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<ISquareClient>(square);
            services.AddSingleton(new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance));
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<RefundStatusService>();
        });
        return (harness, square);
    }

    private static RefundStatusJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, harness.Configuration, Quiet.Logger<RefundStatusJob>());

    private static async Task<Team> SeedSquareTeamAsync(WorkerTickHarness harness)
    {
        var team = await harness.SeedTeamAsync("TESTTEAM");
        await using var dbContext = harness.NewContext();
        var tracked = await dbContext.Teams.FindAsync(team.Id);
        tracked!.SquareAccessToken = "square-token";
        tracked.SquareLocationId = "square-location";
        await dbContext.SaveChangesAsync();
        return tracked;
    }

    /// <param name="squareRefundId">Null models the crash path — the key was persisted, the call never came back.</param>
    private static async Task<int> SeedRefundAsync(
        WorkerTickHarness harness, Team team, RefundStatus status, string? squareRefundId, DateTime? settledUtc = null)
    {
        await using var dbContext = harness.NewContext();
        var user = new User { Name = "System", Email = $"system-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var unmatched = new UnmatchedSquarePayment
        {
            TeamId = team.Id,
            SquareOrderId = $"sq-order-{Guid.NewGuid():N}",
            SquarePaymentId = "sq-payment-1",
            AmountUsd = 15m,
            ReceivedUtc = Now.AddDays(-1)
        };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var refund = new Refund
        {
            TeamId = team.Id,
            UnmatchedSquarePaymentId = unmatched.Id,
            SquarePaymentId = "sq-payment-1",
            AmountUsd = 15m,
            SquareIdempotencyKey = Guid.NewGuid().ToString("N"),
            SquareRefundId = squareRefundId,
            Status = status,
            SettledUtc = settledUtc,
            RequestedByUserId = user.Id,
            RequestedUtc = Now.AddHours(-2)
        };
        dbContext.Refunds.Add(refund);
        await dbContext.SaveChangesAsync();
        return refund.Id;
    }

    private static async Task<Refund> ReloadAsync(WorkerTickHarness harness, int refundId)
    {
        await using var dbContext = harness.NewContext();
        return (await dbContext.Refunds.FindAsync(refundId))!;
    }

    [Fact]
    public async Task APendingRefundThatSquareHasCompletedIsSettled()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Pending, "sq-refund-1");
        square.StatusToReturn = "COMPLETED";

        await CreateJob(harness).RunTickAsync(default);

        Assert.Equal("sq-refund-1", Assert.Single(square.StatusChecks));
        var refund = await ReloadAsync(harness, refundId);
        Assert.Equal(RefundStatus.Completed, refund.Status);
        Assert.Equal(Now, refund.SettledUtc);
    }

    /// <summary>
    /// The outcome the whole job exists to catch. Square accepted the refund, then refused it — and
    /// without this tick the app would still be showing it as issued.
    /// </summary>
    [Fact]
    public async Task ARefundSquareRejectsIsSettledAsRejectedWithAReason()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Pending, "sq-refund-1");
        square.StatusToReturn = "REJECTED";

        await CreateJob(harness).RunTickAsync(default);

        var refund = await ReloadAsync(harness, refundId);
        Assert.Equal(RefundStatus.Rejected, refund.Status);
        Assert.Equal(Now, refund.SettledUtc);
        Assert.Contains("REJECTED", refund.FailureDetail);
    }

    /// <summary>Still pending is the normal answer for days. It must not settle, and it must record that it was asked.</summary>
    [Fact]
    public async Task ARefundStillPendingAtSquareStaysOpenButRecordsTheCheck()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Pending, "sq-refund-1");
        square.StatusToReturn = "PENDING";

        await CreateJob(harness).RunTickAsync(default);

        var refund = await ReloadAsync(harness, refundId);
        Assert.Equal(RefundStatus.Pending, refund.Status);
        Assert.Null(refund.SettledUtc);
        Assert.Equal(Now, refund.LastCheckedUtc);
    }

    /// <summary>
    /// The recovery path. A refund with a persisted key and no refund id is one whose call never
    /// came back — re-sent with the <b>same</b> key, so Square returns the original refund if it
    /// made one and creates it exactly once if it did not.
    /// </summary>
    [Fact]
    public async Task ARefundWhoseCallNeverReturnedIsResentWithItsOriginalKey()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Submitting, squareRefundId: null);
        var originalKey = (await ReloadAsync(harness, refundId)).SquareIdempotencyKey;
        square.StatusToReturn = "COMPLETED";

        await CreateJob(harness).RunTickAsync(default);

        Assert.Equal(originalKey, Assert.Single(square.RefundRequests).IdempotencyKey);
        Assert.Empty(square.StatusChecks);

        var refund = await ReloadAsync(harness, refundId);
        Assert.Equal($"refund-for-{originalKey}", refund.SquareRefundId);
        Assert.Equal(RefundStatus.Completed, refund.Status);
        Assert.Equal(Now, refund.SubmittedUtc);
    }

    /// <summary>Settled means settled — polling one again would be pointless traffic, and a rejected refund re-sent with its key would just be refused a second time.</summary>
    [Fact]
    public async Task AnAlreadySettledRefundIsNotAskedAboutAgain()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        await SeedRefundAsync(harness, team, RefundStatus.Completed, "sq-refund-1", settledUtc: Now.AddHours(-1));

        await CreateJob(harness).RunTickAsync(default);

        Assert.Empty(square.StatusChecks);
        Assert.Empty(square.RefundRequests);
    }

    /// <summary>A failed check leaves the refund exactly as it was, to be asked again next tick — nothing about a network error tells us anything about the refund.</summary>
    [Fact]
    public async Task AFailedStatusCheckLeavesTheRefundOpen()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await SeedSquareTeamAsync(harness);
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Pending, "sq-refund-1");
        square.ThrowOnGet = new HttpRequestException("connection reset");

        await CreateJob(harness).RunTickAsync(default);

        var refund = await ReloadAsync(harness, refundId);
        Assert.Equal(RefundStatus.Pending, refund.Status);
        Assert.Null(refund.SettledUtc);
        Assert.Contains("connection reset", refund.FailureDetail);
    }

    /// <summary>
    /// Optional-integration posture: a team whose Square credentials are gone is skipped quietly and
    /// its refunds stay open, so the first tick after they are restored picks up the whole backlog.
    /// </summary>
    [Fact]
    public async Task ATeamWithNoSquareCredentialsIsSkippedAndItsRefundsStayOpen()
    {
        var (harness, square) = await CreateHarnessAsync();
        await using var _ = harness;
        var team = await harness.SeedTeamAsync("NOSQUARE");
        var refundId = await SeedRefundAsync(harness, team, RefundStatus.Pending, "sq-refund-1");

        await CreateJob(harness).RunTickAsync(default);

        Assert.Empty(square.StatusChecks);
        Assert.Null((await ReloadAsync(harness, refundId)).SettledUtc);
    }

    /// <summary>The tick writes its own JobRunHistory row under the registry's name, which is the join key the admin Job Schedule page reads.</summary>
    [Fact]
    public async Task TheTickRecordsItsRunUnderTheRegisteredJobName()
    {
        var (harness, _) = await CreateHarnessAsync();
        await using var _h = harness;
        await SeedSquareTeamAsync(harness);

        await CreateJob(harness).RunTickAsync(default);

        await using var dbContext = harness.NewContext();
        var run = Assert.Single(await dbContext.JobRunHistories.ToListAsync());
        Assert.Equal(JobSchedules.RefundStatus, run.JobName);
        Assert.True(run.Success);
    }
}
