using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>PaymentReminderService</c>'s local bookkeeping (now just the Unmatched review flag), plus the
/// one money trigger that survived it — the FCC fee reminder.
///
/// <para><b>Both money-side <c>MessageTrigger.PaymentUnpaid</c> pieces are gone (#401, then fully
/// 2026-08-25).</b> Mike: <i>"PaymentUnpaid is literally worthless. If they didn't pay the test
/// session fee, they couldn't test and/or the VEC would not process it."</i> The notice went first;
/// its <c>Payment.ExpiredUnpaid</c> bookkeeping write was kept a round longer on the theory that an
/// unpaid retest fee was still a real case — checked against the real database and found to have
/// never happened, not once, in this deployment's history. Both are gone now: see
/// <c>PaymentReminderService</c>'s own doc comment and CLAUDE.md's "No fee, no test" Known
/// Constraint. There is exactly one real "N days unpaid" trigger left, and it belongs to the FCC's
/// own fee (<c>FccFeeOutstanding</c>), never to this team's own <c>Payment</c>.</para>
/// </summary>
public class PaymentReminderServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
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

    /// <summary>What one day's worth of both jobs did, flattened back into the shape these tests were written against.</summary>
    private sealed record CombinedResult(int RemindersSent, int CandidatesFlaggedForReview, int Failed);

    /// <summary>
    /// Runs the flag pass and then the one remaining money rule.
    /// </summary>
    private sealed class Runner(AppDbContext dbContext, IEmailSender emailSender, int unmatchedReviewWindowDays)
    {
        public async Task<CombinedResult> RunAsync(Team team, CancellationToken cancellationToken)
        {
            var bookkeeping = await new PaymentReminderService(
                dbContext,
                new FixedTimeProvider(Now),
                Options.Create(new PaymentReminderOptions { UnmatchedReviewWindowDays = unmatchedReviewWindowDays }),
                NullLogger<PaymentReminderService>.Instance).RunAsync(team, cancellationToken);

            var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
            var reminders = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, cancellationToken);

            return new CombinedResult(
                reminders.Sent,
                bookkeeping.CandidatesFlaggedForReview,
                reminders.Failed);
        }
    }

    private static Runner CreateService(AppDbContext dbContext, IEmailSender emailSender, int unmatchedReviewWindowDays = 5) =>
        new(dbContext, emailSender, unmatchedReviewWindowDays);

    /// <summary>Records that this team's rule for <paramref name="trigger"/> already fired for a subject — what stops a resend now.</summary>
    private static async Task MarkAlreadyFiredAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, int subjectId, MessageSubjectType subjectType)
    {
        var rule = await dbContext.MessageRules.FirstAsync(r => r.TeamId == team.Id && r.Trigger == trigger);
        dbContext.MessageRuleRuns.Add(new MessageRuleRun
        {
            TeamId = team.Id,
            MessageRuleId = rule.Id,
            RuleName = rule.Name,
            Trigger = trigger,
            SubjectType = subjectType,
            SubjectId = subjectId,
            FiredUtc = Now.AddDays(-1),
            Outcome = MessageRuleOutcome.Sent
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds a Team. emailConfigured=true (default) sets SmtpHost/Username so Team.IsEmailConfigured is true.</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool emailConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task SeedEmailSettingsAndTemplatesAsync(AppDbContext dbContext, Team team)
    {
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        // The two rules that replaced this service's own sends, carrying their own words since
        // 2026-08-21 — they used to point at the two templates seeded just above this. CreatedUtc a
        // year back so it bounds nothing: these tests are about the thresholds, and the bound has its
        // own tests.
        //
        // ⚠️ No payment placeholder anywhere in the FCC-fee body, matching the seeded default. A test
        // body that offered one would let a link creep back into that send path unnoticed
        // (#218/#219) — FCC bills the applicant directly, and the team's Square link pays a different
        // bill.
        var fccFeeReminder = MessageRuleTestHarness.NewRule(
            team, MessageTrigger.FccFeeOutstanding,
            "Hi {{CandidateName}}, session {{SessionDate}}, FRN {{Frn}}", 120, Now.AddYears(-1));
        fccFeeReminder.Subject = "The FCC is waiting for its fee";
        dbContext.MessageRules.Add(fccFeeReminder);

        // No PaymentUnpaid rule any more — see the class summary. Expiration is a plain constant
        // (PaymentReminderService.ExpiryHours) with no rule to seed.
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session/Candidate/Payment(Unpaid, InitialExam, $15) with the given status/date combination.</summary>
    private static async Task<(Candidate Candidate, Payment Payment)> SeedCandidateWithPaymentAsync(
        AppDbContext dbContext,
        Team team,
        CandidateApplicationStatus status = CandidateApplicationStatus.Received,
        DateTime? applicationDateEnteredUtc = null,
        DateTime? dateRegisteredUtc = null,
        SessionStatus sessionStatus = SessionStatus.Active,
        PaymentStatus paymentStatus = PaymentStatus.Unpaid,
        DateTime? paymentReminderSentUtc = null,
        DateTime? scheduledStartUtc = null,
        // What the FCC fee reminder actually keys on (#219). Defaults to PendingVerification —
        // "the FCC fee is due" — because that is the state under test in the reminder half, and the
        // expiration half ignores it entirely.
        FccApplicationPaymentStatus fccPaymentStatus = FccApplicationPaymentStatus.PendingVerification,
        DateTime? fccFeeReminderSentUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = scheduledStartUtc ?? Now.AddDays(-3),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, Status = sessionStatus,
            ZoomJoinUrl = "https://zoom.us/j/123", CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = dateRegisteredUtc ?? Now,
            ApplicationStatus = status, ApplicationDateEnteredUtc = applicationDateEnteredUtc,
            Frn = "0012345678", FccPaymentStatus = fccPaymentStatus, FccFeeReminderSentUtc = fccFeeReminderSentUtc
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = paymentStatus, PaymentLinkUrl = "https://square.link/u/abc", CreatedUtc = Now,
            PaymentReminderSentUtc = paymentReminderSentUtc
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        return (candidate, payment);
    }

    /// <summary>Seeds a Failed Candidate (ResultMarkedUtc set, mirroring MarkFailedAsync) with an Unpaid Reason=Retest Payment — the shape CreateRetestPaymentAsync actually produces.</summary>
    private static async Task<(Candidate Candidate, Payment Payment)> SeedFailedCandidateWithRetestPaymentAsync(
        AppDbContext dbContext,
        Team team,
        DateTime? resultMarkedUtc,
        PaymentStatus paymentStatus = PaymentStatus.Unpaid,
        DateTime? paymentReminderSentUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = Now.AddDays(-3),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, Status = SessionStatus.Active,
            ZoomJoinUrl = "https://zoom.us/j/123", CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now,
            ApplicationStatus = CandidateApplicationStatus.Failed, ResultMarkedUtc = resultMarkedUtc
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.Retest, Amount = 15m,
            Status = paymentStatus, PaymentLinkUrl = "https://square.link/u/retest", CreatedUtc = Now,
            PaymentReminderSentUtc = paymentReminderSentUtc
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        return (candidate, payment);
    }

    // ---- 5-day reminder ----

    /// <summary>
    /// Historical-import safety (2026-08-01). These queries filtered on Session.Status == Active,
    /// which means "not cancelled", never "not finished" — so once SMTP is configured, a year of
    /// backfilled candidates would have received "you haven't paid" emails about sessions they sat
    /// months ago. The seeded session is far past the reminder threshold, so without the
    /// PaymentEligibilityWindow bound this very much fires.
    /// </summary>
    [Fact]
    public async Task Reminder_ForALongPastSession_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(
            dbContext, team, applicationDateEnteredUtc: Now.AddDays(-190), scheduledStartUtc: Now.AddDays(-200));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
        Assert.Null((await dbContext.Candidates.SingleAsync()).FccFeeReminderSentUtc);
    }

    [Fact]
    public async Task Reminder_ExactlyFiveDaysSinceApplicationDateEntered_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        Assert.NotNull((await dbContext.Candidates.SingleAsync()).FccFeeReminderSentUtc);
    }

    [Fact]
    public async Task Reminder_BeforeFiveDays_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-4));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task Reminder_AfterFiveDays_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-6));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_AlreadySent_DoesNotResend()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8), fccFeeReminderSentUtc: Now.AddDays(-3));
        // The run marker is what suppresses it now, not Candidate.FccFeeReminderSentUtc — that column
        // is still written and still shown in the candidate's email history, but nothing reads it to
        // decide. See MessageRuleEngineTests for the same point on the registration confirmation.
        await MarkAlreadyFiredAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, candidate.Id, MessageSubjectType.Candidate);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task Reminder_UnmatchedCandidate_NeverFires_NoApplicationDate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, applicationDateEnteredUtc: null);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_GrantedCandidate_TerminalStatusSkipsIt()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Granted, applicationDateEnteredUtc: Now.AddDays(-8));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    /// <summary>
    /// Terminal statuses are excluded whatever ULS still says. A Failed candidate has no live
    /// application, so a lingering PendingVerification is stale data rather than a bill.
    /// </summary>
    [Fact]
    public async Task Reminder_FailedCandidate_StillSkipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Failed, applicationDateEnteredUtc: Now.AddDays(-8));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    /// <summary>
    /// The retest branch went with the payment it hung off (#219). A retest has no FCC application
    /// of its own, so there is no FCC fee to chase — and the elaborate ResultMarkedUtc anchoring
    /// that existed to make retests work in the old exam-fee reminder now has nothing to anchor.
    /// Its candidate is Failed, which the terminal exclusion already covers.
    /// </summary>
    [Fact]
    public async Task Reminder_RetestPayment_NoLongerFires_ThereIsNoFccFeeForARetest()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedFailedCandidateWithRetestPaymentAsync(dbContext, team, resultMarkedUtc: Now.AddDays(-6));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// The correction itself, stated as a test. The team's exam fee being unpaid is no longer the
    /// trigger, so a candidate the FCC is happy with is not chased — however the Square row looks.
    /// Before #219 this sent, days after the session, about money already collected at it.
    /// </summary>
    [Theory]
    [InlineData(FccApplicationPaymentStatus.Paid)]
    [InlineData(FccApplicationPaymentStatus.Unknown)]
    public async Task Reminder_UnpaidExamFee_DoesNotFire_WhenTheFccFeeIsNotOutstanding(FccApplicationPaymentStatus fccStatus)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team,
            applicationDateEnteredUtc: Now.AddDays(-8),
            paymentStatus: PaymentStatus.Unpaid,
            fccPaymentStatus: fccStatus);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// And the converse: the Square payment's state is now irrelevant to this reminder. A candidate
    /// who settled the exam fee at the session — the normal case — still hears about FCC's fee.
    /// </summary>
    [Fact]
    public async Task Reminder_PaidExamFee_StillFires_BecauseTheFccFeeIsADifferentBill()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team,
            applicationDateEnteredUtc: Now.AddDays(-8), paymentStatus: PaymentStatus.Paid);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
    }

    /// <summary>
    /// A team that collects no fees has no Payment row at all, and its candidates still owe the FCC.
    /// The old reminder could never reach them; scanning Candidates is what makes this possible, and
    /// it is the reason the tracking stamp had to move off Payment.
    /// </summary>
    [Fact]
    public async Task Reminder_FiresForACandidateWithNoPaymentRowAtAll()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, payment) = await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8));
        dbContext.Payments.Remove(payment);
        await dbContext.SaveChangesAsync();
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).FccFeeReminderSentUtc);
    }

    /// <summary>
    /// #218 by construction rather than by patch: the body cannot contain an empty payment href
    /// because no payment placeholder is offered to it. The rendered text is asserted, not just the
    /// fact of a send — the original bug shipped under a green "sent 1, failed 0".
    /// </summary>
    [Fact]
    public async Task Reminder_BodyCarriesNoPaymentLink_AndNamesTheFrnCoresWillAskFor()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8));
        var sender = new FakeEmailSender();

        await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("0012345678", message.HtmlBody);
        Assert.DoesNotContain("square.link", message.HtmlBody);
        Assert.DoesNotContain("href=\"\"", message.HtmlBody);
    }

    [Fact]
    public async Task Reminder_CancelledSession_Skipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8), sessionStatus: SessionStatus.Cancelled);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    // ⚠️ Reminder_MissingTemplate_CountsAsFailed_IsRetryable was deleted here on 2026-08-21, for the
    // same reason as its twin in MessageRuleEngineTests: a message owns its own words, so a rule
    // cannot point at a template that is not there. The failure it covered is unreachable.


    // ⚠️ The whole "10-day expiration" test section — Expiration_ExactlyTenDays_Fires_SetsExpiredUnpaid,
    // _BeforeTenDays_DoesNotFire, _AfterTenDays_Fires, _AlreadyExpired_DoesNotResend,
    // _GrantedCandidate_TerminalStatusSkipsIt, _FailedCandidateInitialExamPayment_StillSkipped,
    // _RetestPayment_FiresTenDaysAfterResultMarked_SetsExpiredUnpaid,
    // _RetestPayment_BeforeTenDaysSinceResultMarked_DoesNotFire, _NotApplicablePayment_Skipped,
    // _CancelledSession_Skipped, and ReminderAndExpiration_BothOverdueInOneRun_BothFire — was deleted
    // here on 2026-08-25, along with Payment.ExpiredUnpaid and the write that set it. There is
    // nothing left to test: the pass they exercised no longer exists. See the class summary and
    // PaymentReminderService's own doc comment.

    // ---- Unmatched review flag ----

    [Fact]
    public async Task UnmatchedFlag_ExactlyWindowDays_Flags()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).UnmatchedReviewFlaggedUtc);
    }

    [Fact]
    public async Task UnmatchedFlag_BeforeWindow_DoesNotFlag()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-4));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_AlreadyFlagged_DoesNotReflag()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-10));
        candidate.UnmatchedReviewFlaggedUtc = Now.AddDays(-2);
        await dbContext.SaveChangesAsync();
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_ReceivedCandidate_NotFlagged()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Received, dateRegisteredUtc: Now.AddDays(-10), applicationDateEnteredUtc: Now.AddDays(-10));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_CustomWindow_RespectsConfiguredValue()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-3));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender, unmatchedReviewWindowDays: 3).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_RunsEvenWhenNoEmailSettingsRowExists()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Deliberately no EmailSettings row seeded — reminders/expirations get skipped, but the
        // flag pass doesn't send email at all, so it should still run.
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).UnmatchedReviewFlaggedUtc);
        Assert.Empty(sender.SentMessages);
    }

    // ---- SMTP not configured ----

    // ⚠️ Expiration_FollowsTheTeamsOwnRule_NotTheOldTenDayConstant was deleted here on 2026-08-25.
    // Its whole premise — a team's PaymentUnpaid rule could push the expiry threshold out to 30
    // days — no longer applies: there is no rule, and no expiration pass to push it on.

    /// <summary>
    /// The Unmatched review flag needs no SMTP — it never sends email, only logs and stamps a field.
    /// </summary>
    [Fact]
    public async Task SmtpNotConfigured_SendsNothing_ButStillFlags()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, emailConfigured: false);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (unmatchedCandidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-10));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
        Assert.Empty(dbContext.MessageRuleRuns);
        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == unmatchedCandidate.Id)).UnmatchedReviewFlaggedUtc);
    }

    // ---- PII purge ----

    [Fact]
    public async Task PurgedCandidate_ExcludedFromReminderAndFlagPasses()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8));
        candidate.PiiPurgedUtc = Now;
        candidate.Email = null;
        await dbContext.SaveChangesAsync();
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }
}
