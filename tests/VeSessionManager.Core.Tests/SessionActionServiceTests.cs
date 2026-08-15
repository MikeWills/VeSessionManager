using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SessionActionServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];
        public HashSet<string> FailingRecipients { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailingRecipients.Contains(message.ToAddress))
            {
                throw new InvalidOperationException($"Simulated SMTP failure sending to {message.ToAddress}");
            }

            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        // #375 added these to ISquareClient. Not exercised here — throwing rather than returning a
        // stub keeps that true: if this test ever starts refunding, it says so instead of passing
        // against a fake that quietly agrees.
        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public List<string> CompletedOrderIds { get; } = [];

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionActionServiceTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken)
        {
            CompletedOrderIds.Add(orderId);
            return Task.CompletedTask;
        }

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionActionServiceTests.");
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // emailSender is kept in the signature although this service no longer sends anything (#221):
    // every call site passes one, and the two tests below assert that nothing is sent, which needs a
    // sender to observe.
    private static SessionActionService CreateService(AppDbContext dbContext, IEmailSender emailSender, ISquareClient? squareClient = null) => new(
        dbContext,
        new SquarePaymentMatchingService(dbContext, squareClient ?? new FakeSquareClient(), new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<SquarePaymentMatchingService>.Instance),
        new FixedTimeProvider(Now),
        NullLogger<SessionActionService>.Instance);

    private static async Task<(Team Team, User User, Session Session)> SeedSessionAsync(AppDbContext dbContext, bool emailConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            CreatedUtc = Now
        };
        var user = new User { Name = "Session Manager", Email = "sm@example.com", Role = UserRole.SessionManager };
        var vec = new Vec { Name = "ARRL" };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "Test Session", ScheduledStartUtc = Now,
            Team = team, Vec = vec, FeeConfiguration = feeConfiguration, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "FelonyDisclosureInstructions", Subject = "FCC steps required",
            Body = "Hi {{CandidateName}}, additional FCC steps are required."
        });
        await dbContext.SaveChangesAsync();

        return (team, user, session);
    }

    private static Candidate AddCandidate(AppDbContext dbContext, Session session, CandidateApplicationStatus status, bool? hasFelonyDisclosure = null, bool tested = false)
    {
        var candidate = new Candidate
        {
            SessionId = session.Id, Name = "Test Candidate", Email = "candidate@example.com",
            DateRegisteredUtc = Now, ApplicationStatus = status, Tested = tested, HasFelonyDisclosure = hasFelonyDisclosure
        };
        dbContext.Candidates.Add(candidate);
        dbContext.SaveChanges();
        return candidate;
    }

    // ---- MarkCompletedAsync ----

    [Fact]
    public async Task MarkCompleted_FlipsNonTerminalCandidatesToTested_LeavesTerminalOnesAlone()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        var unmatched = AddCandidate(dbContext, session, CandidateApplicationStatus.Unmatched);
        var received = AddCandidate(dbContext, session, CandidateApplicationStatus.Received);
        var alreadyFailed = AddCandidate(dbContext, session, CandidateApplicationStatus.Failed);
        var alreadyNotTested = AddCandidate(dbContext, session, CandidateApplicationStatus.NotTested);

        var result = await CreateService(dbContext, new FakeEmailSender()).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result.Result);
        Assert.Equal(2, result.CandidatesTested);
        Assert.True(dbContext.Candidates.Single(c => c.Id == unmatched.Id).Tested);
        Assert.True(dbContext.Candidates.Single(c => c.Id == received.Id).Tested);
        Assert.False(dbContext.Candidates.Single(c => c.Id == alreadyFailed.Id).Tested);
        Assert.False(dbContext.Candidates.Single(c => c.Id == alreadyNotTested.Id).Tested);
        var updatedSession = dbContext.Sessions.Single();
        Assert.Equal(Now, updatedSession.TestingCompletedUtc);
        Assert.Equal(user.Id, updatedSession.TestingCompletedByUserId);
    }

    /// <summary>
    /// The inversion (#221). This used to assert an email went out; the point now is that none does.
    /// Marking a session complete is a bulk status flip, and "your felony disclosure requires extra
    /// FCC paperwork" is not something to say to anyone as a side effect of one.
    /// </summary>
    [Fact]
    public async Task MarkCompleted_CandidateWithFelonyDisclosure_SendsNothing()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        var withDisclosure = AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: false);

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Empty(sender.SentMessages);
        Assert.Null((await dbContext.Candidates.FindAsync(withDisclosure.Id))!.FelonyDisclosureInstructionsSentUtc);
        // The status flip is untouched by the removal.
        Assert.True((await dbContext.Candidates.FindAsync(withDisclosure.Id))!.Tested);
    }

    /// <summary>
    /// Removing the automatic send is exactly what could leave someone with nothing, so the count of
    /// people still owed the instructions comes back with the result and the page says so.
    /// </summary>
    [Fact]
    public async Task MarkCompleted_ReportsHowManyStillNeedTheFelonyInstructions()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: false);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(2, result.CandidatesAwaitingFelonyInstructions);
    }

    /// <summary>Someone already sent them by hand is not still waiting.</summary>
    [Fact]
    public async Task MarkCompleted_DoesNotCountACandidateWhoAlreadyGotTheInstructions()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        var alreadySent = AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        alreadySent.FelonyDisclosureInstructionsSentUtc = Now.AddDays(-2);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(0, result.CandidatesAwaitingFelonyInstructions);
    }

    [Fact]
    public async Task MarkCompleted_AlreadyCompleted_ReturnsAlreadyDone_NoDoubleFlip()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        session.TestingCompletedUtc = Now.AddDays(-1);
        session.TestingCompletedByUserId = user.Id;
        await dbContext.SaveChangesAsync();
        AddCandidate(dbContext, session, CandidateApplicationStatus.Unmatched);

        var result = await CreateService(dbContext, new FakeEmailSender()).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.AlreadyDone, result.Result);
        Assert.False(dbContext.Candidates.Single().Tested);
    }

    [Fact]
    public async Task MarkCompleted_CandidateAlreadyPaidBeforeSession_CompletesSquareOrder()
    {
        // The payment arrived before the session was marked done — the other direction
        // (SquarePaymentMatchingService completing eligible orders right when a payment gets
        // matched) is covered in SquarePaymentMatchingServiceTests.
        await using var dbContext = CreateContext();
        var (team, user, session) = await SeedSessionAsync(dbContext);
        team.SquareAccessToken = "sq-token";
        team.SquareLocationId = "sq-location";
        var candidate = AddCandidate(dbContext, session, CandidateApplicationStatus.Received);
        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Paid, PaidDateUtc = Now.AddDays(-1),
            SquarePaymentReferenceId = "order-already-paid", CreatedUtc = Now.AddDays(-1)
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var square = new FakeSquareClient();
        var result = await CreateService(dbContext, new FakeEmailSender(), square).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result.Result);
        Assert.Equal("order-already-paid", Assert.Single(square.CompletedOrderIds));
        Assert.Equal(Now, dbContext.Payments.Single().SquareOrderCompletedUtc);
    }

    // ---- ClearRescheduleFlagAsync ----

    [Fact]
    public async Task ClearRescheduleFlag_WhenFlagged_ClearsItAndAudits()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        session.RescheduleFlaggedForReview = true;
        session.RescheduleFlaggedUtc = Now.AddDays(-1);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).ClearRescheduleFlagAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result);
        Assert.False(dbContext.Sessions.Single().RescheduleFlaggedForReview);
        // The timestamp used to be left behind, so a cleared session still read as flagged to
        // anything looking at the column rather than the bool. This test already seeded the
        // timestamp and simply never asserted on it, which is why it survived.
        Assert.Null(dbContext.Sessions.Single().RescheduleFlaggedUtc);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "RescheduleFlagCleared");
    }

    [Fact]
    public async Task ClearRescheduleFlag_WhenNotFlagged_IsNoOp()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext, new FakeEmailSender()).ClearRescheduleFlagAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.AlreadyDone, result);
        Assert.Empty(dbContext.AuditLogs);
    }

    // ---- SetRetainedAmountOverrideAsync ----

    [Fact]
    public async Task SetRetainedAmountOverride_SetsValueAndAuditsAndTracksActingUser()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext, new FakeEmailSender()).SetRetainedAmountOverrideAsync(session.Id, 20m, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result);
        var updated = dbContext.Sessions.Single();
        Assert.Equal(20m, updated.RetainedAmountOverride);
        Assert.Equal(user.Id, updated.RetainedAmountOverrideByUserId);
        Assert.Equal(Now, updated.RetainedAmountOverrideUtc);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "SessionRetainedAmountOverrideSet");
    }

    [Fact]
    public async Task SetRetainedAmountOverride_Null_ClearsExistingOverride()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        session.RetainedAmountOverride = 20m;
        session.RetainedAmountOverrideByUserId = user.Id;
        session.RetainedAmountOverrideUtc = Now;
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).SetRetainedAmountOverrideAsync(session.Id, null, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result);
        var updated = dbContext.Sessions.Single();
        Assert.Null(updated.RetainedAmountOverride);
        Assert.Null(updated.RetainedAmountOverrideByUserId);
        Assert.Null(updated.RetainedAmountOverrideUtc);
    }

    [Fact]
    public async Task SetRetainedAmountOverride_SessionNotFound_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var (_, user, _) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext, new FakeEmailSender()).SetRetainedAmountOverrideAsync(9999, 20m, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.NotFound, result);
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task Delete_RemovesSessionCandidatesPaymentsAndVeRoster_AndAudits()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        var candidate = AddCandidate(dbContext, session, CandidateApplicationStatus.Received);
        dbContext.Payments.Add(new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Unpaid, CreatedUtc = Now
        });
        var ve = new VolunteerExaminer { CallSign = "W1AW", Name = "Test VE" };
        dbContext.VolunteerExaminers.Add(ve);
        await dbContext.SaveChangesAsync();
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = ve.Id });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).DeleteAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result.Result);
        Assert.Equal(1, result.CandidatesRemoved);
        Assert.Equal(1, result.PaymentsRemoved);
        Assert.Equal(1, result.VeAssignmentsRemoved);
        Assert.Empty(dbContext.Sessions);
        Assert.Empty(dbContext.Candidates);
        Assert.Empty(dbContext.Payments);
        Assert.Empty(dbContext.SessionVolunteerExaminers);
        // The VE itself is a team-wide roster record, not session-owned — only the join row is removed.
        Assert.Single(dbContext.VolunteerExaminers);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "SessionDeleted");
    }

    [Fact]
    public async Task Delete_NonExistentSession_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var (_, user, _) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext, new FakeEmailSender()).DeleteAsync(999, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.NotFound, result.Result);
    }

    [Fact]
    public async Task Delete_PaymentStillReferencedByUnmatchedSquarePayment_IsBlocked_DeletesNothing()
    {
        await using var dbContext = CreateContext();
        var (team, user, session) = await SeedSessionAsync(dbContext);
        var candidate = AddCandidate(dbContext, session, CandidateApplicationStatus.Received);
        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Paid, PaidDateUtc = Now, CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        dbContext.UnmatchedSquarePayments.Add(new UnmatchedSquarePayment
        {
            TeamId = team.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-payment-1", AmountUsd = 15m, ReceivedUtc = Now,
            MatchedPaymentId = payment.Id
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, new FakeEmailSender()).DeleteAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Blocked, result.Result);
        Assert.Single(dbContext.Sessions);
        Assert.Single(dbContext.Candidates);
        Assert.Single(dbContext.Payments);
        Assert.Empty(dbContext.AuditLogs);
    }
}
