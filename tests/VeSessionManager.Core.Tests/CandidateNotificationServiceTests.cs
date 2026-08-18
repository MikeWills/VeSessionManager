using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class CandidateNotificationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string TestPublicBaseUrl = "https://test.example";

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];
        public List<EmailCredentials> CredentialsUsed { get; } = [];
        public Exception? ThrowOnNextSend { get; set; }

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            if (ThrowOnNextSend is not null)
            {
                var ex = ThrowOnNextSend;
                ThrowOnNextSend = null;
                throw ex;
            }
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

    private static CandidateNotificationService CreateService(AppDbContext dbContext, IEmailSender emailSender) => new(
        dbContext,
        new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
        emailSender, new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
        new FixedTimeProvider(Now),
        Options.Create(new AppOptions { PublicBaseUrl = TestPublicBaseUrl }),
        NullLogger<CandidateNotificationService>.Instance);

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
            FromDisplayName = "VE Session Manager",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "RegistrationConfirmation",
            Subject = "Registered for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}} ({{CandidateName}}), Zoom: {{ZoomJoinUrl}}, Pay: {{PaymentLinkUrl}}, Privacy: {{PrivacyPolicyUrl}}"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "DayBeforeReminder",
            Subject = "Reminder for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}}, Zoom: {{ZoomJoinUrl}}, Outstanding: {{OutstandingPaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session, returning the Session for further per-test customization.</summary>
    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, bool feeCollectionEnabled = true,
        SessionStatus status = SessionStatus.Active, string? zoomJoinUrl = "https://zoom.us/j/123", bool supportsYouthProgram = false)
    {
        var vec = new Vec { Name = "ARRL", SupportsYouthProgram = supportsYouthProgram };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = feeCollectionEnabled, ExamFeeAmount = feeCollectionEnabled ? 15m : null,
            CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, Status = status,
            ZoomJoinUrl = zoomJoinUrl, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static Candidate NewCandidate(Session session, string applicantId = "applicant-1", string firstName = "Roana", string lastName = "Glory") => new()
    {
        ExamToolsApplicantId = applicantId,
        SessionId = session.Id,
        Name = $"{firstName} {lastName}",
        FirstName = firstName,
        Email = $"{firstName.ToLower()}@example.com",
        DateRegisteredUtc = Now
    };

    [Fact]
    public async Task ResendRegistrationConfirmationAsync_SessionAlreadyEnded_StillSends()
    {
        // The manual, admin-triggered "resend" action is unaffected by the past-session guard —
        // a human explicitly clicking resend means it regardless of the session's date.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-15));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).ResendRegistrationConfirmationAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        Assert.Single(sender.SentMessages);
    }

    /// <summary>
    /// <b>#417: a hand-send is a run now.</b> Before this, pressing a button recorded the send only in
    /// a <c>Candidate.*SentUtc</c> column, which is what forced the candidate's email history to carry
    /// per-column fallbacks and a dedup rule (#415). A button is a perfectly good trigger; it is just
    /// not a scheduled one.
    /// </summary>
    [Fact]
    public async Task ResendRegistrationConfirmationAsync_RecordsARunSoTheSendIsVisibleInHistory()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(5));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeEmailSender())
            .ResendRegistrationConfirmationAsync(candidate.Id, CancellationToken.None);

        var run = Assert.Single(dbContext.MessageRuleRuns);
        Assert.Equal(candidate.Id, run.SubjectId);
        Assert.Equal(MessageSubjectType.Candidate, run.SubjectType);
        Assert.Equal(MessageRuleOutcome.Sent, run.Outcome);
        Assert.Equal(CandidateNotificationService.ResentConfirmationLabel, run.RuleName);
        // No rule produced it — the same nullable column that lets a run outlive a deleted rule.
        Assert.Null(run.MessageRuleId);
        // The message *is* a registration confirmation, however it was set off.
        Assert.Equal(MessageTrigger.CandidateRegistered, run.Trigger);
    }

    /// <summary>
    /// The Youth Program instructions mirror no trigger point — nothing can send them on a scan — so
    /// they carry the marker value rather than borrowing a trigger that would read as a lie.
    /// </summary>
    [Fact]
    public async Task SendYouthProgramInstructionsAsync_RecordsARunMarkedSentByHand()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(5));
        session.Vec.SupportsYouthProgram = true;
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "ArrlYouthProgramInstructions", Subject = "Youth Program", Body = "Hi"
        });
        var candidate = NewCandidate(session);
        candidate.CallSign = "KE0ABC";
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeEmailSender())
            .SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None);

        var run = Assert.Single(dbContext.MessageRuleRuns);
        Assert.Equal(MessageTrigger.SentByHand, run.Trigger);
        Assert.Equal(CandidateNotificationService.YouthProgramLabel, run.RuleName);
    }

    /// <summary>
    /// A send that never happened records nothing. The run is written inside the funnel, after the
    /// send — so a missing template leaves no trace claiming otherwise, which is the same property
    /// #396 was about.
    /// </summary>
    [Fact]
    public async Task AFailedSend_RecordsNoRun()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Deliberately no templates seeded.
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(5));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeEmailSender())
            .ResendRegistrationConfirmationAsync(candidate.Id, CancellationToken.None);

        Assert.Empty(dbContext.MessageRuleRuns);
    }

    // ---- Youth Program instructions ----

    [Fact]
    public async Task YouthProgramInstructions_VecSupportsIt_SendsAndMarksSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        session.Vec.SupportsYouthProgram = true;
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "ArrlYouthProgramInstructions", Subject = "Youth Program",
            Body = "Hi {{CandidateName}} ({{CallSign}})"
        });
        var candidate = NewCandidate(session);
        candidate.CallSign = "KE0ABC";
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("KE0ABC", message.HtmlBody);
        // Display-only for the session detail page's "Email history" modal — this action has no
        // send cap, so unlike RegistrationConfirmationSentUtc this always holds the latest send.
        Assert.Equal(Now, dbContext.Candidates.Single().YouthProgramInstructionsSentUtc);
    }

    [Fact]
    public async Task YouthProgramInstructions_VecDoesNotSupportIt_NotSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.VecDoesNotSupportYouthProgram, result);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().YouthProgramInstructionsSentUtc);
    }

    // ---- Felony disclosure instructions: manual since #221 ----

    private static async Task<(Team Team, Candidate Candidate)> SeedForFelonyAsync(AppDbContext dbContext, bool? hasFelonyDisclosure, bool tested = false)
    {
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "FelonyDisclosureInstructions", Subject = "Additional FCC steps",
            Body = "Hi {{CandidateName}}, additional FCC steps are required."
        });
        var candidate = NewCandidate(session);
        candidate.HasFelonyDisclosure = hasFelonyDisclosure;
        candidate.Tested = tested;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return (team, candidate);
    }

    /// <summary>
    /// The change that matters (#221): before, this could only ever go out after the session, because
    /// it rode along with marking one complete. The information is worth having beforehand, while
    /// there is still a Session Manager to ask about it — so nothing here looks at Tested.
    /// </summary>
    [Fact]
    public async Task FelonyDisclosureInstructions_SendBeforeTheSession_Sends()
    {
        await using var dbContext = CreateContext();
        var (_, candidate) = await SeedForFelonyAsync(dbContext, hasFelonyDisclosure: true, tested: false);

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendFelonyDisclosureInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("additional FCC steps are required", message.HtmlBody);
        Assert.Equal(Now, dbContext.Candidates.Single().FelonyDisclosureInstructionsSentUtc);
    }

    /// <summary>
    /// The boundary, and the reason the check lives in the service rather than only in the page. The
    /// candidate id arrives from a form now, and this email tells someone their felony disclosure
    /// requires extra FCC paperwork — the wrong recipient is not a cosmetic error.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task FelonyDisclosureInstructions_NoDisclosureDeclared_Refused(bool? hasFelonyDisclosure)
    {
        await using var dbContext = CreateContext();
        var (_, candidate) = await SeedForFelonyAsync(dbContext, hasFelonyDisclosure);

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendFelonyDisclosureInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.NoFelonyDisclosure, result);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().FelonyDisclosureInstructionsSentUtc);
    }

    /// <summary>
    /// No send cap — a second click is a deliberate re-send, and the stamp holds the latest one so
    /// the page can say when it last went out. Same shape as the youth-program action.
    /// </summary>
    [Fact]
    public async Task FelonyDisclosureInstructions_CanBeSentAgain_StampHoldsTheLatest()
    {
        await using var dbContext = CreateContext();
        var (_, candidate) = await SeedForFelonyAsync(dbContext, hasFelonyDisclosure: true);
        var service = CreateService(dbContext, new FakeEmailSender());
        await service.SendFelonyDisclosureInstructionsAsync(candidate.Id, CancellationToken.None);

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendFelonyDisclosureInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        Assert.Single(sender.SentMessages);
    }

    // ---- Email switched off: reported, not silently swallowed (#396) ----

    /// <summary>
    /// Each of these used to return <see cref="CandidateEmailSendResult.Sent"/> for a muted team and
    /// send nothing, because the send path they shared with the scan-based jobs had to answer
    /// "nothing more to do" for a job's benefit — right for a poll pass, and a lie to somebody
    /// standing at a button. The jobs are rules now (#401), so this can be the true answer.
    ///
    /// <para>Written as a theory over all three because the failure mode is a missed call site: two
    /// of them checking and one not is exactly how it looked before.</para>
    /// </summary>
    [Theory]
    [InlineData("resend")]
    [InlineData("youth")]
    [InlineData("felony")]
    public async Task EmailSwitchedOff_EveryOnDemandAction_ReportsItRatherThanClaimingSuccess(string action)
    {
        await using var dbContext = CreateContext();
        var (team, candidate) = await SeedForFelonyAsync(dbContext, hasFelonyDisclosure: true);
        // Both switches: the per-integration ones only apply while the master override is on.
        team.IntegrationOverridesEnabled = true;
        team.EmailEnabled = false;
        dbContext.Vecs.Single().SupportsYouthProgram = true;
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = team.Id, Key = "ArrlYouthProgramInstructions", Subject = "Youth", Body = "Youth" });
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = team.Id, Key = "RegistrationConfirmation", Subject = "Registered", Body = "Registered" });
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var service = CreateService(dbContext, sender);
        var result = action switch
        {
            "resend" => await service.ResendRegistrationConfirmationAsync(candidate.Id, CancellationToken.None),
            "youth" => await service.SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None),
            _ => await service.SendFelonyDisclosureInstructionsAsync(candidate.Id, CancellationToken.None)
        };

        Assert.Equal(CandidateEmailSendResult.EmailMuted, result);
        Assert.Empty(sender.SentMessages);
    }
}
