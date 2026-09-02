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

    private static async Task SeedSystemSettingsAsync(
        AppDbContext dbContext, int? piiRetentionWindowDays, int? veContactRetentionYears = null)
    {
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            PiiRetentionWindowDays = piiRetentionWindowDays,
            VeContactRetentionYears = veContactRetentionYears,
            UlsWatcherIntervalHours = 24,
            UlsWatcherStartHourEt = 8
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Team/Session/Candidate/Payment with the given status/date combination.</summary>
    private static async Task<(Candidate Candidate, Payment Payment)> SeedCandidateAsync(
        AppDbContext dbContext,
        CandidateApplicationStatus status,
        DateTime? licenseGrantDateUtc = null,
        DateTime? sessionScheduledStartUtc = null,
        DateTime? piiPurgedUtc = null,
        string? name = "Roana Glory",
        string? firstName = null)
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
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = name, FirstName = firstName,
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
        // Session date set before the grant date so this models a genuine new grant (the common
        // case), not the "already licensed before this session" upgrade shape covered separately below.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, payment) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-30), sessionScheduledStartUtc: Now.Date.AddDays(-35));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.GrantedCandidatesPurged);
        var purged = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Null(purged.Name);
        Assert.Null(purged.Email);
        Assert.Equal("1234567890", purged.Frn); // FRN is public FCC data, retained for traceability (2026-08-03)
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
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-29), sessionScheduledStartUtc: Now.Date.AddDays(-35));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerA_OneDayAfterThreshold_Purges()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-31), sessionScheduledStartUtc: Now.Date.AddDays(-35));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.GrantedCandidatesPurged);
        Assert.Null((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerA_AlreadyPurged_NotReprocessed()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-90), sessionScheduledStartUtc: Now.Date.AddDays(-95), piiPurgedUtc: Now.AddDays(-1));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
    }

    [Fact]
    public async Task TriggerA_LicenseGrantPredatesSession_AnchorsOnSessionDate_NotPurgedEarly()
    {
        // Found live 2026-07-28 (real HRCC data): an existing licensee re-testing (upgrade or repeat)
        // gets matched against their own already-old license — FCC's Grant Date doesn't change on a
        // class upgrade. Without the session-date floor, this candidate's PII would purge almost
        // immediately after a real, current session just because their original grant is old.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-90), sessionScheduledStartUtc: Now.Date.AddDays(-3));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
    }

    [Fact]
    public async Task TriggerA_LicenseGrantPredatesSession_PurgesOnceSessionDateThresholdPassed()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-90), sessionScheduledStartUtc: Now.Date.AddDays(-30));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.GrantedCandidatesPurged);
        Assert.Null((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).Name);
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
        Assert.Equal("1234567890", purged.Frn); // FRN is public FCC data, retained for traceability (2026-08-03)
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
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-31), sessionScheduledStartUtc: Now.Date.AddDays(-35));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.EntityId == candidate.Id && a.EntityType == nameof(Candidate));
        Assert.Null(audit.UserId);
        Assert.Equal("CandidatePiiPurged", audit.Action);
        Assert.Contains("Trigger A", audit.Details);

        // #86 part 3. A job has no acting user to scope through, so without the team on the row this
        // entry is invisible to every TeamAdmin — filtered out by AdminAccessScope.ScopeAuditLog not
        // as a decision but because nothing matched. The purge is deployment-wide; the entry is not.
        Assert.Equal(candidate.Session.TeamId, audit.TeamId);
    }

    [Fact]
    public async Task Purge_LicenseGrantPredatesSession_AuditLogNamesSessionDateAnchor()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            licenseGrantDateUtc: Now.Date.AddDays(-90), sessionScheduledStartUtc: Now.Date.AddDays(-30));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.EntityId == candidate.Id && a.EntityType == nameof(Candidate));
        Assert.Contains("Trigger A", audit.Details);
        Assert.Contains("pre-existing license", audit.Details);
        Assert.Contains("anchored on session date", audit.Details);
    }

    // ---- repair pass: rows purged before FirstName joined the purge definition (T02, 2026-08-03) ----

    /// <summary>Seeds the exact shape the repair pass exists for: PiiPurgedUtc stamped and Name
    /// already null (purged under the old definition), but FirstName still holding a given name.</summary>
    private static Task<(Candidate Candidate, Payment Payment)> SeedIncompletelyPurgedCandidateAsync(
        AppDbContext dbContext, DateTime originalPurgedUtc) =>
        SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            sessionScheduledStartUtc: Now.Date.AddDays(-60),
            piiPurgedUtc: originalPurgedUtc,
            name: null,
            firstName: "Roana");

    [Fact]
    public async Task RunAsync_AlreadyPurgedCandidateStillHoldingFirstName_IsRepaired()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyPurgedCandidatesRepaired);
        var repaired = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Null(repaired.FirstName);
        Assert.Null(repaired.Name);
        Assert.Null(repaired.Email);
    }

    [Fact]
    public async Task RunAsync_RepairingAnAlreadyPurgedCandidate_PreservesTheOriginalPurgeDate()
    {
        // The purge date records when retention actually expired, not when this repair happened to
        // run — restamping it would silently reset the record for every affected row.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var originalPurgedUtc = Now.AddDays(-40);
        var (candidate, _) = await SeedIncompletelyPurgedCandidateAsync(dbContext, originalPurgedUtc);

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var repaired = await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id);
        Assert.Equal(originalPurgedUtc, repaired.PiiPurgedUtc);
    }

    [Fact]
    public async Task RunAsync_RepairPass_IsIdempotent_SecondRunRepairsNothing()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));
        var service = CreateService(dbContext);

        var first = await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, first.AlreadyPurgedCandidatesRepaired);
        Assert.Equal(0, second.AlreadyPurgedCandidatesRepaired);
    }

    [Fact]
    public async Task RunAsync_RepairPass_DoesNotCountAgainstTheTriggerCounters()
    {
        // A repaired row is already purged — it must not be reported as a fresh Trigger A/B purge.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.GrantedCandidatesPurged);
        Assert.Equal(0, result.FailedCandidatesPurged);
    }

    [Fact]
    public async Task RunAsync_RepairPass_AlsoClearsAnyLiveSquareLinkLeftOnThePayment()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (_, payment) = await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var repairedPayment = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Null(repairedPayment.PaymentLinkUrl);
        Assert.Null(repairedPayment.SquarePaymentReferenceId);
    }

    [Fact]
    public async Task RunAsync_RepairPass_WritesAnAuditLogEntry()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        var (candidate, _) = await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.EntityId == candidate.Id && a.EntityType == nameof(Candidate));
        Assert.Equal("CandidatePiiPurged", audit.Action);
        Assert.Null(audit.UserId);
        Assert.Contains("re-cleared", audit.Details);
    }

    [Fact]
    public async Task RunAsync_FullyPurgedCandidate_IsNotRepairedAgain()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: 30);
        await SeedCandidateAsync(dbContext, CandidateApplicationStatus.Granted,
            sessionScheduledStartUtc: Now.Date.AddDays(-60), piiPurgedUtc: Now.AddDays(-40),
            name: null, firstName: null);

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.AlreadyPurgedCandidatesRepaired);
    }

    [Fact]
    public async Task RunAsync_NotConfigured_DoesNotRunTheRepairPassEither()
    {
        // The repair pass sits behind the same early return as the purge triggers.
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, piiRetentionWindowDays: null);
        var (candidate, _) = await SeedIncompletelyPurgedCandidateAsync(dbContext, Now.AddDays(-40));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.AlreadyPurgedCandidatesRepaired);
        Assert.Equal("Roana", (await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).FirstName);
    }

    // ---- #313 / L-07: a retired VE's contact details age out ----

    /// <summary>Seeds a VE with contact details, optionally on a team and optionally having worked a session.</summary>
    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, bool activeMembership, DateTime? lastWorkedUtc, DateTime? createdUtc = null)
    {
        var team = new Team { Name = "HRCC", ExamToolsTeamCode = "HRCC", CreatedUtc = Now };
        var person = new VolunteerExaminer
        {
            Name = "Pat Examiner",
            CallSign = "W0PAT",
            Email = "pat@example.org",
            Phone = "555-0100",
            AddressLine1 = "12 Private Road",
            City = "Mankato",
            State = "MN",
            PostalCode = "56001",
            DiscordUsername = "pat-1234",
            DiscordUserId = 1170000000000000009,
            Notes = "Prefers Saturday sessions",
            CreatedUtc = createdUtc ?? Now.AddYears(-20)
        };
        dbContext.Teams.Add(team);
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(new VeTeamMembership
        {
            VolunteerExaminer = person, Team = team, IsActive = activeMembership, CreatedUtc = Now.AddYears(-20)
        });

        if (lastWorkedUtc is { } worked)
        {
            var vec = new Vec { Name = "ARRL" };
            var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
            var session = new Session
            {
                ExamToolsSessionId = "s-" + Guid.NewGuid(),
                Title = "A session",
                ScheduledStartUtc = worked,
                DurationMinutes = 60,
                Team = team,
                Vec = vec,
                FeeConfiguration = new FeeConfiguration
                {
                    Vec = vec,
                    EffectiveDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    FeeCollectionEnabled = false,
                    CreatedByUser = user,
                    CreatedUtc = Now.AddYears(-20)
                },
                Status = SessionStatus.Active,
                CreatedUtc = Now.AddYears(-20)
            };
            dbContext.Sessions.Add(session);
            dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer
            {
                Session = session, VolunteerExaminer = person
            });
        }

        await dbContext.SaveChangesAsync();
        return person;
    }

    private static void AssertContactDetailsCleared(VolunteerExaminer person)
    {
        Assert.Null(person.Email);
        Assert.Null(person.Phone);
        Assert.Null(person.AddressLine1);
        Assert.Null(person.City);
        Assert.Null(person.State);
        Assert.Null(person.PostalCode);
        Assert.Null(person.DiscordUsername);

        // The id is the same fact as the username, and the stronger one: clearing the label while
        // keeping a permanent handle on the person's Discord account would defeat the purge (#519).
        Assert.Null(person.DiscordUserId);

        Assert.Null(person.Notes);
        Assert.NotNull(person.PiiPurgedUtc);
    }

    [Fact]
    public async Task RetiredVe_InactiveBeyondTheWindow_HasContactDetailsCleared()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var person = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: Now.AddYears(-6));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.VolunteerExaminersPurged);
        AssertContactDetailsCleared(person);
    }

    /// <summary>
    /// The accreditation trail survives. Clearing it would destroy the record that this person was
    /// qualified to administer the exams they administered, which is why this is a field-level purge
    /// and not a row delete.
    /// </summary>
    [Fact]
    public async Task PurgingAVeKeepsTheirNameCallSignAndHistory()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var person = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: Now.AddYears(-6));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal("Pat Examiner", person.Name);
        Assert.Equal("W0PAT", person.CallSign);
        Assert.Single(dbContext.SessionVolunteerExaminers.Where(l => l.VolunteerExaminerId == person.Id));
    }

    /// <summary>
    /// Both halves of "inactive" matter. A current roster member who has had a quiet few years is
    /// still a current volunteer — losing the address their team invites them with would be a bug,
    /// not a purge.
    /// </summary>
    [Fact]
    public async Task ActiveTeamMember_IsNeverPurged_HoweverLongSinceTheyWorked()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var person = await SeedVeAsync(dbContext, activeMembership: true, lastWorkedUtc: Now.AddYears(-20));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersPurged);
        Assert.Equal("pat@example.org", person.Email);
    }

    [Fact]
    public async Task RetiredVe_InsideTheWindow_IsNotPurged()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var person = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: Now.AddYears(-2));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersPurged);
        Assert.Equal("pat@example.org", person.Email);
    }

    /// <summary>
    /// The two-condition rule from the opposite direction: a VE who has never worked a session has
    /// no last-worked date at all. Anchoring on CreatedUtc means an imported row that went nowhere
    /// still ages out.
    /// </summary>
    [Fact]
    public async Task NeverWorkedAndOffEveryRoster_AgesOutOnCreatedDate()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var old = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: null, createdUtc: Now.AddYears(-9));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.VolunteerExaminersPurged);
        AssertContactDetailsCleared(old);
    }

    /// <summary>And the case that makes the CreatedUtc fallback safe rather than dangerous.</summary>
    [Fact]
    public async Task RecentlyAddedVe_WhoHasNotWorkedYet_IsNotPurged()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        var person = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: null, createdUtc: Now.AddDays(-3));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersPurged);
        Assert.Equal("pat@example.org", person.Email);
    }

    /// <summary>
    /// Nothing is purged until an admin sets a window. Same explicit-opt-in rule as the candidate
    /// side, and a stronger case for it — nobody expects a volunteer roster to start forgetting
    /// people because a job shipped.
    /// </summary>
    [Fact]
    public async Task WithNoWindowConfigured_NoVeIsPurged()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: null);
        var person = await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: Now.AddYears(-20));

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersPurged);
        Assert.Equal("pat@example.org", person.Email);
    }

    /// <summary>
    /// Audited, with no contact details in the entry — writing them into the audit log on the way
    /// out would defeat the point of clearing them.
    /// </summary>
    [Fact]
    public async Task PurgingAVeIsAudited_WithoutRestatingTheDetails()
    {
        await using var dbContext = CreateContext();
        await SeedSystemSettingsAsync(dbContext, 90, veContactRetentionYears: 5);
        await SeedVeAsync(dbContext, activeMembership: false, lastWorkedUtc: Now.AddYears(-6));

        await CreateService(dbContext).RunAsync(CancellationToken.None);

        var audit = Assert.Single(dbContext.AuditLogs.Where(a => a.Action == "VolunteerExaminerPiiPurged"));
        Assert.DoesNotContain("pat@example.org", audit.Details);
        Assert.DoesNotContain("Private Road", audit.Details);
    }
}
