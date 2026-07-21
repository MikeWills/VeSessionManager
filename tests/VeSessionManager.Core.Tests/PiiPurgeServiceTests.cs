using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.PiiPurge;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class PiiPurgeServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static PiiPurgeService CreateService(AppDbContext dbContext) => new(
        dbContext,
        new SystemSettingsService(dbContext, new FixedTimeProvider(Now)),
        new FixedTimeProvider(Now),
        NullLogger<PiiPurgeService>.Instance);

    private static async Task SeedSystemSettingsAsync(AppDbContext dbContext, int? piiRetentionWindowDays)
    {
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            PiiRetentionWindowDays = piiRetentionWindowDays,
            FccDailyWatcherIntervalHours = 24,
            FccWeeklyCatchupIntervalHours = 24,
            FccWeeklyCatchupDayOfWeek = DayOfWeek.Monday
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Team/Session/Candidate/Payment with the given status/date combination.</summary>
    private static async Task<(Candidate Candidate, Payment Payment)> SeedCandidateAsync(
        AppDbContext dbContext,
        CandidateApplicationStatus status,
        DateTime? licenseGrantDateUtc = null,
        DateTime? sessionScheduledStartUtc = null,
        DateTime? piiPurgedUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session",
            ScheduledStartUtc = sessionScheduledStartUtc ?? Now.AddDays(-3),
            DurationMinutes = 60, Vec = vec, Team = team, FeeConfiguration = feeConfiguration, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", Frn = "1234567890", HasFelonyDisclosure = false,
            DateRegisteredUtc = Now.AddDays(-30), ApplicationStatus = status,
            LicenseGrantDateUtc = licenseGrantDateUtc, CallSign = "N0CALL",
            PiiPurgedUtc = piiPurgedUtc
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Paid, PaymentLinkUrl = "https://square.link/u/abc",
            SquarePaymentReferenceId = "sq-order-1", CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        return (candidate, payment);
    }

    // ---- not configured ----

    [Fact]
    public async Task NotConfigured_NoOp_NeverPurges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: null);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.AddDays(-90));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.Equal(0, result.FailedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    // ---- Trigger A: passed candidates ----

    [Fact]
    public async Task TriggerA_ExactlyAtThreshold_Purges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, payment) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.Date.AddDays(-30));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.GrantedCandidatesPurged);
        var purged = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Null(purged.Name);
        Assert.Null(purged.Email);
        Assert.Null(purged.Frn);
        Assert.Null(purged.HasFelonyDisclosure);
        Assert.NotNull(purged.PiiPurgedUtc);
        var purgedPayment = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Null(purgedPayment.PaymentLinkUrl);
        Assert.Null(purgedPayment.SquarePaymentReferenceId);
    }

    [Fact]
    public async Task TriggerA_OneDayBeforeThreshold_DoesNotPurge()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.Date.AddDays(-29));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerA_OneDayAfterThreshold_Purges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.Date.AddDays(-31));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.GrantedCandidatesPurged);
        Assert.Null((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerA_AlreadyPurged_NotReprocessed()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.Date.AddDays(-90), piiPurgedUtc: Now.AddDays(-1));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
    }

    // ---- Trigger B: failed candidates ----

    [Fact]
    public async Task TriggerB_ExactlyAtThreshold_Purges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, payment) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Failed, sessionScheduledStartUtc: Now.Date.AddDays(-30));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedCandidatesPurged);
        var purged = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Null(purged.Name);
        Assert.Null(purged.Email);
        Assert.Null(purged.Frn);
        Assert.Null(purged.HasFelonyDisclosure);
        Assert.NotNull(purged.PiiPurgedUtc);
        // Non-PII fields untouched.
        Assert.Equal("N0CALL", purged.CallSign);
        Assert.Equal(CandidateApplicationStatus.Failed, purged.ApplicationStatus);
        var untouchedPayment = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(15m, untouchedPayment.Amount);
        Assert.Equal(PaymentStatus.Paid, untouchedPayment.Status);
        Assert.Equal(PaymentReason.InitialExam, untouchedPayment.Reason);
    }

    [Fact]
    public async Task TriggerB_OneDayBeforeThreshold_DoesNotPurge()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Failed, sessionScheduledStartUtc: Now.Date.AddDays(-29));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.FailedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerB_OneDayAfterThreshold_Purges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Failed, sessionScheduledStartUtc: Now.Date.AddDays(-31));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedCandidatesPurged);
        Assert.Null((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerB_AlreadyPurged_NotReprocessed()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Failed, sessionScheduledStartUtc: Now.Date.AddDays(-90), piiPurgedUtc: Now.AddDays(-1));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.FailedCandidatesPurged);
    }

    // ---- excluded states ----

    [Theory]
    [InlineData(CandidateApplicationStatus.Unmatched)]
    [InlineData(CandidateApplicationStatus.Received)]
    public async Task NonTerminalStatus_NeverPurged_RegardlessOfAge(CandidateApplicationStatus status)
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, status, sessionScheduledStartUtc: Now.AddDays(-3650));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.Equal(0, result.FailedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task NotTested_NeverPurgedByThisJob_AlreadyHandledImmediatelyElsewhere()
    {
        // NotTested candidates already have their PII nulled immediately by Phase 9's delete/no-show
        // action (CandidateActionService), not on this scheduled window — this job must never
        // re-trigger a purge for one regardless of how old its session is.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.NotTested, sessionScheduledStartUtc: Now.AddDays(-3650));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.Equal(0, result.FailedCandidatesPurged);
    }

    // ---- audit ----

    [Fact]
    public async Task Purge_WritesAuditLogEntry_NamingTheTrigger()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted, licenseGrantDateUtc: Now.Date.AddDays(-31));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.EntityId == candidate.Id && a.EntityType == nameof(Candidate));
        Assert.Null(audit.UserId);
        Assert.Equal("CandidatePiiPurged", audit.Action);
        Assert.Contains("Trigger A", audit.Details);
    }
}
