using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Square;
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
        public List<string> CompletedOrderIds { get; } = [];

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionActionServiceTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken)
        {
            CompletedOrderIds.Add(orderId);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static SessionActionService CreateService(AppDbContext dbContext, IEmailSender emailSender, ISquareClient? squareClient = null) => new(
        dbContext,
        new CandidateNotificationService(
            dbContext,
            new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
            emailSender,
            new FixedTimeProvider(Now),
            NullLogger<CandidateNotificationService>.Instance),
        new SquarePaymentMatchingService(dbContext, squareClient ?? new FakeSquareClient(), new FixedTimeProvider(Now), NullLogger<SquarePaymentMatchingService>.Instance),
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

    [Fact]
    public async Task MarkCompleted_CandidateWithFelonyDisclosure_SendsInstructionsEmail()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        var withDisclosure = AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: false);

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(1, result.FelonyDisclosureEmailsSent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("additional FCC steps are required", message.HtmlBody);
        // FelonyDisclosureInstructionsSentUtc is a display-only timestamp (session detail page's
        // "Email history" modal) — the send itself is guarded by MarkCompletedAsync's own
        // one-shot "candidates just tested" set, not by this field.
        Assert.NotNull((await dbContext.Candidates.FindAsync(withDisclosure.Id))!.FelonyDisclosureInstructionsSentUtc);
    }

    [Fact]
    public async Task MarkCompleted_OneFelonyDisclosureEmailFails_DoesNotThrow_StillSendsToOtherCandidates()
    {
        // One candidate's SMTP failure must not stop the rest of the batch, nor bubble up and make
        // the whole "mark completed" action look like it failed when the status flip already
        // succeeded and was saved.
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true).Email = "fails@example.com";
        var succeeds = AddCandidate(dbContext, session, CandidateApplicationStatus.Received, hasFelonyDisclosure: true);
        succeeds.Email = "succeeds@example.com";
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        sender.FailingRecipients.Add("fails@example.com");

        var result = await CreateService(dbContext, sender).MarkCompletedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(SessionActionResult.Success, result.Result);
        Assert.Equal(2, result.CandidatesTested);
        Assert.Equal(1, result.FelonyDisclosureEmailsSent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("succeeds@example.com", message.ToAddress);
        // The status flip itself must not have been rolled back by the later email failure.
        Assert.True(dbContext.Candidates.Single(c => c.Email == "fails@example.com").Tested);
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
}
