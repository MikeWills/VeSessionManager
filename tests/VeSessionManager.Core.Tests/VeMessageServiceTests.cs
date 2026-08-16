using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Writing to a team's VEs from the directory (#191).
///
/// <para>The scoping test is the one that matters. This service's sibling shipped the unscoped
/// version of exactly this query (#238): an id posted from a form reached any VolunteerExaminer row on
/// the deployment, and the mail went out over the team's own SMTP — genuine in every observable way,
/// which is what made it worth fixing rather than noting.</para>
/// </summary>
public class VeMessageServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeBatchEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public int BatchCalls { get; private set; }
        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(message.ToAddress))
            {
                throw new InvalidOperationException($"Refused: {message.ToAddress}");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<EmailSendOutcome>> SendManyAsync(
            EmailCredentials credentials, IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
        {
            BatchCalls++;
            var outcomes = new List<EmailSendOutcome>(messages.Count);
            foreach (var message in messages)
            {
                try
                {
                    await SendAsync(credentials, message, cancellationToken);
                    outcomes.Add(EmailSendOutcome.Success);
                }
                catch (Exception ex)
                {
                    outcomes.Add(new EmailSendOutcome(false, ex));
                }
            }

            return outcomes;
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static VeMessageService CreateService(AppDbContext dbContext, IEmailSender emailSender) => new(
        dbContext,
        new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
        emailSender,
        new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
        new VeUnsubscribeService(
            dbContext,
            new FixedTimeProvider(Now),
            Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }),
            NullLogger<VeUnsubscribeService>.Instance),
        new FixedTimeProvider(Now),
        NullLogger<VeMessageService>.Instance);

    private const string PublicBaseUrl = "https://test.example";

    private static async Task<(Team Team, User User)> SeedTeamAsync(
        AppDbContext dbContext, string name = "TEAMA", bool emailConfigured = true, bool emailSettings = true, bool emailEnabled = true)
    {
        var team = new Team
        {
            Name = name,
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            // Both, or nothing is muted: the per-integration switches are only consulted while
            // IntegrationOverridesEnabled is true (#64).
            IntegrationOverridesEnabled = !emailEnabled,
            EmailEnabled = emailEnabled,
            CreatedUtc = Now
        };
        var user = new User { Name = "Sender", Email = "sender@example.org", Role = UserRole.TeamAdmin };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        if (emailSettings)
        {
            dbContext.EmailSettings.Add(new EmailSettings
            {
                TeamId = team.Id,
                FromAddress = "noreply@example.org",
                FromDisplayName = "Team A VEs",
                ReplyToAddress = "reply@example.org",
                PrivacyPolicyUrl = "https://example.org/privacy",
                AdminNotificationEmail = "admin@example.org",
                BccAddress = "watch@example.org"
            });
            await dbContext.SaveChangesAsync();
        }

        return (team, user);
    }

    private static async Task<VolunteerExaminer> AddVeAsync(
        AppDbContext dbContext, Team team, string name, string? email, string? callSign = null,
        bool active = true, VeContactPreference preference = VeContactPreference.Email)
    {
        var ve = new VolunteerExaminer { Name = name, Email = email, CallSign = callSign, ContactPreference = preference };
        dbContext.VolunteerExaminers.Add(ve);
        await dbContext.SaveChangesAsync();

        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = ve.Id, TeamId = team.Id, IsActive = active });
        await dbContext.SaveChangesAsync();
        return ve;
    }

    private const string Subject = "Field Day, {{CallSign}}";
    private const string Body = "<p>Hi {{VeName}}, from {{TeamName}}.</p>";

    [Fact]
    public async Task SendsToEveryChosenVe_RenderedForEachOne()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ana = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com", "N0ABC");
        var bo = await AddVeAsync(dbContext, team, "Bo Chen", "bo@example.com", "N0XYZ");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ana.Id, bo.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Sent);
        var toAna = sender.Sent.Single(m => m.ToAddress == "ana@example.com");
        Assert.Equal("Field Day, N0ABC", toAna.Subject);
        Assert.Contains("Hi Ana Ruiz, from TEAMA.", toAna.HtmlBody);
        Assert.Contains("Hi Bo Chen", sender.Sent.Single(m => m.ToAddress == "bo@example.com").HtmlBody);
    }

    [Fact]
    public async Task AVeOnAnotherTeam_IsDroppedNotMailed()
    {
        // #238, which this service's sibling shipped: the ids come from a form, so the screen's own
        // list is a default rather than a constraint.
        await using var dbContext = CreateContext();
        var (teamA, user) = await SeedTeamAsync(dbContext);
        var (teamB, _) = await SeedTeamAsync(dbContext, "TEAMB");
        var mine = await AddVeAsync(dbContext, teamA, "Ana Ruiz", "ana@example.com");
        var theirs = await AddVeAsync(dbContext, teamB, "Not Mine", "elsewhere@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            teamA.Id, [mine.Id, theirs.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NotOnTeam);
        Assert.DoesNotContain(sender.Sent, m => m.ToAddress == "elsewhere@example.com");
    }

    [Fact]
    public async Task ARetiredMember_IsDroppedNotMailed()
    {
        // Retired from this team is not "on the roster" — the same filter GetRecipientsAsync uses, so
        // the screen and the send cannot disagree about who is reachable.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var retired = await AddVeAsync(dbContext, team, "Retired VE", "retired@example.com", active: false);
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [retired.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Equal(1, result.NotOnTeam);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task AVeNameCarryingMarkup_IsEncoded()
    {
        // #260 in its original place: this is the service whose sibling hand-rolled substitution and
        // rendered a session title as live markup in every recipient's mail client.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "</p><a href=\"https://evil/\">Click</a>", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.DoesNotContain("<a href=\"https://evil/\">", message.HtmlBody);
        Assert.Contains("&lt;a href=", message.HtmlBody);
    }

    [Fact]
    public async Task ATextOnlyVe_IsSkippedAndCounted()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var textOnly = await AddVeAsync(dbContext, team, "Text Only", "text@example.com", preference: VeContactPreference.Text);
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [textOnly.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Equal(1, result.TextOnlySkipped);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task AVeWithNoAddress_IsCountedRatherThanSilentlySkipped()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ana = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var noAddress = await AddVeAsync(dbContext, team, "No Address", null);
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ana.Id, noAddress.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NoEmailAddress);
    }

    [Fact]
    public async Task AMutedTeam_SendsNothing_AndSaysSo()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext, emailEnabled: false);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData("", "<p>b</p>")]
    [InlineData("s", "   ")]
    public async Task ABlankSubjectOrBody_SendsNothing(string subject, string body)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], subject, body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UnconfiguredSmtpOrMissingSettings_SendsNothing()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext, emailConfigured: false);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task OneRefusedAddress_DoesNotStopTheRest_AndTravelsAsOneBatch()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ana = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var bo = await AddVeAsync(dbContext, team, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();
        sender.FailFor.Add("ana@example.com");

        var result = await CreateService(dbContext, sender).SendAsync(
            team.Id, [ana.Id, bo.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, sender.BatchCalls);
    }

    [Fact]
    public async Task SendsFromTheTeamsOwnAddresses_AndCarriesNoMonitoringBcc()
    {
        // The Bcc is for candidate-facing mail (#207). A VE is a member of the team that would be
        // watching, not somebody it is corresponding with.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("noreply@example.org", message.FromAddress);
        Assert.Equal("reply@example.org", message.ReplyToAddress);
        Assert.Null(message.BccAddress);
    }

    [Fact]
    public async Task WritesOneAuditRowForTheBatch()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ana = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var bo = await AddVeAsync(dbContext, team, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendAsync(
            team.Id, [ana.Id, bo.Id], Subject, Body, "Field Day invite", user.Id, CancellationToken.None);

        var audit = Assert.Single(await dbContext.AuditLogs.ToListAsync());
        Assert.Equal("VeMessageSent", audit.Action);
        Assert.Equal(user.Id, audit.UserId);
    }

    [Fact]
    public async Task GetRecipientsAsync_ListsActiveMembersAndFlagsWhoCannotBeReached()
    {
        await using var dbContext = CreateContext();
        var (team, _) = await SeedTeamAsync(dbContext);
        await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        await AddVeAsync(dbContext, team, "No Address", null);
        await AddVeAsync(dbContext, team, "Text Only", "text@example.com", preference: VeContactPreference.Text);
        await AddVeAsync(dbContext, team, "Retired VE", "retired@example.com", active: false);
        var sender = new FakeBatchEmailSender();

        var recipients = await CreateService(dbContext, sender).GetRecipientsAsync(team.Id, CancellationToken.None);

        Assert.Equal(3, recipients.Count);
        Assert.DoesNotContain(recipients, r => r.VolunteerExaminer.Name == "Retired VE");
        Assert.True(recipients.Single(r => r.VolunteerExaminer.Name == "Ana Ruiz").CanReceive);
        Assert.False(recipients.Single(r => r.VolunteerExaminer.Name == "No Address").CanReceive);
        Assert.False(recipients.Single(r => r.VolunteerExaminer.Name == "Text Only").CanReceive);
    }

    // ---- Unsubscribe (#191, CAN-SPAM) ----------------------------------------------------------

    private static VeUnsubscribeService CreateUnsubscribeService(AppDbContext dbContext) => new(
        dbContext,
        new FixedTimeProvider(Now),
        Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }),
        NullLogger<VeUnsubscribeService>.Instance);

    [Fact]
    public async Task EveryMessageCarriesAnUnsubscribeLink_EvenWhenTheDraftForgetsOne()
    {
        // The reason it is appended rather than required: an unsubscribe that depends on somebody
        // remembering a placeholder is one that will eventually be missing from a real send.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, "<p>No placeholder here.</p>", "Field Day invite", user.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Contains($"{PublicBaseUrl}/ve/unsubscribe/", message.HtmlBody);
        Assert.Contains("Unsubscribe", message.HtmlBody);
    }

    [Fact]
    public async Task ADraftPlacingTheTokenItself_IsNotGivenASecondFooter()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendAsync(
            team.Id, [ve.Id], Subject, "<p>Stop: <a href=\"{{UnsubscribeUrl}}\">here</a></p>",
            "Field Day invite", user.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        // One link, where the author put it.
        Assert.Equal(1, message.HtmlBody.Split($"{PublicBaseUrl}/ve/unsubscribe/").Length - 1);
        Assert.DoesNotContain("You are receiving this because", message.HtmlBody);
    }

    [Fact]
    public async Task TheSameVeKeepsOneUnsubscribeLinkAcrossSends()
    {
        // CAN-SPAM wants the mechanism working for at least 30 days after a message. Re-minting per
        // send would break the link in every email already delivered — the exact failure the rule is
        // about — so the token is minted once and reused.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();
        var service = CreateService(dbContext, sender);

        await service.SendAsync(team.Id, [ve.Id], Subject, Body, "First", user.Id, CancellationToken.None);
        await service.SendAsync(team.Id, [ve.Id], Subject, Body, "Second", user.Id, CancellationToken.None);

        static string LinkIn(string body) => body.Split("/ve/unsubscribe/")[1].Split('"')[0].Split('<')[0].Trim();
        Assert.Equal(LinkIn(sender.Sent[0].HtmlBody), LinkIn(sender.Sent[1].HtmlBody));
    }

    [Fact]
    public async Task AnUnsubscribedVe_IsNotMailedAgain_AndIsCounted()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();
        var service = CreateService(dbContext, sender);

        await service.SendAsync(team.Id, [ve.Id], Subject, Body, "First", user.Id, CancellationToken.None);
        var link = sender.Sent[0].HtmlBody.Split("/ve/unsubscribe/")[1].Split('"')[0].Split('<')[0].Trim();
        Assert.True(await CreateUnsubscribeService(dbContext).UnsubscribeAsync(link, CancellationToken.None));

        sender.Sent.Clear();
        var result = await service.SendAsync(team.Id, [ve.Id], Subject, Body, "Second", user.Id, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Unsubscribed);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UnsubscribingTwice_IsNotAnError()
    {
        // Somebody clicking the link in two different old emails must not see a failure page.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();
        await CreateService(dbContext, sender).SendAsync(team.Id, [ve.Id], Subject, Body, "First", user.Id, CancellationToken.None);
        var link = sender.Sent[0].HtmlBody.Split("/ve/unsubscribe/")[1].Split('"')[0].Split('<')[0].Trim();
        var service = CreateUnsubscribeService(dbContext);

        Assert.True(await service.UnsubscribeAsync(link, CancellationToken.None));
        Assert.True(await service.UnsubscribeAsync(link, CancellationToken.None));

        Assert.Single(await dbContext.AuditLogs.Where(a => a.Action == "VeEmailUnsubscribed").ToListAsync());
    }

    [Fact]
    public async Task ResubscribingRestoresEmail()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedTeamAsync(dbContext);
        var ve = await AddVeAsync(dbContext, team, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();
        var service = CreateService(dbContext, sender);
        await service.SendAsync(team.Id, [ve.Id], Subject, Body, "First", user.Id, CancellationToken.None);
        var link = sender.Sent[0].HtmlBody.Split("/ve/unsubscribe/")[1].Split('"')[0].Split('<')[0].Trim();
        var unsubscribe = CreateUnsubscribeService(dbContext);
        await unsubscribe.UnsubscribeAsync(link, CancellationToken.None);

        await unsubscribe.ResubscribeAsync(link, CancellationToken.None);

        sender.Sent.Clear();
        var result = await service.SendAsync(team.Id, [ve.Id], Subject, Body, "Second", user.Id, CancellationToken.None);
        Assert.Equal(1, result.Sent);
    }

    [Fact]
    public async Task AnUnknownToken_ResolvesToNobody()
    {
        await using var dbContext = CreateContext();
        var service = CreateUnsubscribeService(dbContext);

        Assert.Null(await service.ResolveAsync("not-a-real-token", CancellationToken.None));
        Assert.False(await service.UnsubscribeAsync("not-a-real-token", CancellationToken.None));
    }
}
