using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The Email candidates screen (#144) — composing one message and sending it to candidates chosen on
/// a session.
///
/// <para>The authorization tests are the point of this file. Both the session id and the candidate
/// ids arrive from the request, and the message is sent from the team's own SMTP with its own
/// From/Reply-To — so a gap here is not "someone sees a page they shouldn't", it is attacker-authored
/// mail that is genuinely from the team. Each test builds its own factory because the seeded database
/// is shared per-factory and these mutate it.</para>
/// </summary>
public class CandidateEmailPageTests
{
    private const string TemplateKey = "GettingStartedLocally";

    private static async Task<int> SeedTemplateAsync(WebAppFactory factory, string subject, string body)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var template = new EmailTemplate
        {
            TeamId = factory.Seeded.TeamId,
            Key = TemplateKey,
            Subject = subject,
            Body = body
        };
        db.EmailTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    /// <summary>
    /// Gives the seeded team what it needs to actually send: SMTP credentials and a From/Reply-To row.
    /// The harness deliberately seeds neither, so a send-path test has to ask for them — which is also
    /// the state a fresh deployment is in.
    /// </summary>
    private static async Task ConfigureTeamEmailAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var team = await db.Teams.FirstAsync(t => t.Id == factory.Seeded.TeamId);
        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";

        db.EmailSettings.Add(new EmailSettings
        {
            TeamId = factory.Seeded.TeamId,
            FromAddress = "noreply@example.org",
            FromDisplayName = "Test Team",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A second team with its own session and candidate — the "not mine" side of every scope test.</summary>
    private static async Task<(int SessionId, int CandidateId)> SeedOtherTeamSessionAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var team = new Team { Name = "OTHER-TEAM", ExamToolsTeamCode = "OTHER" };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        var session = new Session
        {
            TeamId = team.Id,
            VecId = factory.Seeded.VecId,
            FeeConfigurationId = (await db.FeeConfigurations.FirstAsync()).Id,
            ExamToolsSessionId = "et-other-team-session",
            Title = "Another team's session",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(-3),
            DurationMinutes = 60,
            Status = SessionStatus.Active
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var candidate = new Candidate
        {
            SessionId = session.Id,
            Name = "Other Team Candidate",
            FirstName = "Other",
            Email = "other@example.com",
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-10)
        };
        db.Candidates.Add(candidate);
        await db.SaveChangesAsync();
        return (session.Id, candidate.Id);
    }

    /// <summary>
    /// Demotes the seeded account in the database, which is what actually decides scope:
    /// <c>SessionAccessScope.CanEdit</c> reads the loaded <c>User</c> row, not the request's role
    /// header, so a client created "as TeamAdmin" against a SystemAdmin row still reaches every team.
    /// The header alone would make these tests pass without a guard behind them.
    /// </summary>
    private static async Task DemoteSeededUserToTeamAdminAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == factory.Seeded.UserId);
        user.Role = UserRole.TeamAdmin;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Posts the way a browser does, token and all. Razor Pages validate antiforgery in middleware,
    /// so a POST without one is a 400 before the page is reached — which would make every
    /// authorization test here pass for entirely the wrong reason.
    ///
    /// <para>The token is taken from the compose screen for the caller's <b>own</b> session, then
    /// posted wherever the test aims it. That is precisely the tampering case: a real signed-in user
    /// with a valid token, aiming it at a session or a candidate the screen never offered them.</para>
    /// </summary>
    private static async Task<HttpResponseMessage> PostWithTokenAsync(
        HttpClient client, WebAppFactory factory, string url, IEnumerable<int> candidateIds,
        string subject = "Hi", string body = "<p>Hi</p>")
    {
        var page = await client.GetStringAsync($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}");
        var token = System.Text.RegularExpressions.Regex
            .Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Subject", subject),
            new("Body", body),
            // "template", not "SelectedTemplateKey": the page binds it under the query-string name it
            // uses on GET, and posting the property's own name binds nothing.
            new("template", TemplateKey),
            new("__RequestVerificationToken", token)
        };
        fields.AddRange(candidateIds.Select(id => new KeyValuePair<string, string>("SelectedCandidateIds", id.ToString())));
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task ChoosingATemplate_FillsTheDraftFromThatTeamsCurrentText()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Welcome, {{CandidateFirstName}}", "<p>Our club meets Tuesdays.</p>");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}?template={TemplateKey}");

        Assert.Contains("Welcome, {{CandidateFirstName}}", html);
        Assert.Contains("Our club meets Tuesdays.", html);
        // The tokens stay tokens in the draft: one message is composed for many recipients, so there
        // is no single candidate to resolve them against until send.
        Assert.Contains("{{CandidateFirstName}}", html);
    }

    [Fact]
    public async Task TheScreenListsTheSessionsCandidates_AndSaysWhoCannotBeReached()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Subject", "<p>Body</p>");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Candidates.Add(new Candidate
            {
                SessionId = factory.Seeded.SessionId,
                Name = "No Address",
                FirstName = "No",
                Email = null,
                DateRegisteredUtc = DateTime.UtcNow.AddDays(-9)
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}");

        Assert.Contains("Test Candidate", html);
        Assert.Contains("No Address", html);
        Assert.Contains("No email", html);
    }

    [Fact]
    public async Task ASessionOnAnotherTeam_IsRefusedOnGet()
    {
        using var factory = new WebAppFactory();
        var (otherSessionId, _) = await SeedOtherTeamSessionAsync(factory);
        // A SystemAdmin legitimately reaches every team, so the account has to be demoted for this
        // to be a test of anything.
        await DemoteSeededUserToTeamAdminAsync(factory);
        var client = factory.CreateClientAs(UserRole.TeamAdmin);

        var response = await client.GetAsync($"/SessionManager/CandidateEmail/{otherSessionId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ASessionOnAnotherTeam_IsRefusedOnPost()
    {
        // The GET refusal is not the guard — a POST never has to go through it.
        using var factory = new WebAppFactory();
        var (otherSessionId, otherCandidateId) = await SeedOtherTeamSessionAsync(factory);
        await DemoteSeededUserToTeamAdminAsync(factory);
        var client = factory.CreateClientAs(UserRole.TeamAdmin);

        var response = await PostWithTokenAsync(
            client, factory, $"/SessionManager/CandidateEmail/{otherSessionId}", [otherCandidateId]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ACandidateTheScreenNeverOffered_IsRefused()
    {
        // #238's shape: the ids come from the form, so the page re-checks them against what it
        // actually listed rather than trusting the browser. The service re-scopes independently.
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Subject", "<p>Body</p>");
        var (_, otherCandidateId) = await SeedOtherTeamSessionAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostWithTokenAsync(
            client, factory, $"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}",
            [factory.Seeded.CandidateId, otherCandidateId]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.CandidateEmailSends.ToListAsync());
    }

    [Fact]
    public async Task PostingWithNobodySelected_SendsNothingAndSaysSo()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Subject", "<p>Body</p>");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostWithTokenAsync(
            client, factory, $"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}", []);

        // Back to the compose screen rather than through to a send.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.CandidateEmailSends.ToListAsync());
    }

    /// <summary>
    /// The history records the template the draft started from, which is what the compose screen's
    /// "already sent" column reads back.
    ///
    /// <para><b>This does not guard the hidden field's name</b>, which is how the label actually
    /// broke: the helper posts a hand-built body, so the markup could name the field anything and
    /// this would still pass — verified by reverting the fix and watching it stay green. A form-field
    /// name is only checkable by submitting the rendered form or by scanning the source, which is
    /// <c>FormBindingTests</c>'s whole job, and it is what caught this. What this test pins is the
    /// other half: that a label posted correctly survives the service and reaches the row.</para>
    /// </summary>
    [Fact]
    public async Task TheSendIsRecordedUnderTheTemplateItStartedFrom()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Subject", "<p>Body</p>");
        await ConfigureTeamEmailAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        await PostWithTokenAsync(
            client, factory, $"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}", [factory.Seeded.CandidateId]);

        var sent = Assert.Single(factory.SentEmails);
        Assert.Equal("candidate@localhost", sent.ToAddress);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var send = Assert.Single(await db.CandidateEmailSends.ToListAsync());
        Assert.Equal("Getting started locally", send.TemplateLabel);
    }

    [Fact]
    public async Task TheSessionPageLinksToTheScreen()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.Contains($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}", html);
    }

    [Fact]
    public async Task AHandSentEmail_ShowsInTheCandidatesEmailHistory()
    {
        using var factory = new WebAppFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CandidateEmailSends.Add(new CandidateEmailSend
            {
                CandidateId = factory.Seeded.CandidateId,
                TemplateLabel = "Getting started locally",
                SentUtc = new DateTime(2026, 8, 15, 14, 30, 0, DateTimeKind.Utc),
                SentByUserId = factory.Seeded.UserId
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.Contains("Getting started locally", html);
    }

    [Fact]
    public async Task TheSessionPageOffersEachTemplateAsAShortcut_AndKeepsThePlainButton()
    {
        // "Keep the existing place as well" (Mike, 2026-08-16): the menu is a shortcut into the same
        // screen, not a replacement for opening it.
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, "Welcome", "<p>Body</p>");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        // The plain button, unchanged.
        Assert.Contains($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}\"", html);
        // And the same destination with a template already chosen.
        Assert.Contains($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}?template={TemplateKey}", html);
        Assert.Contains("Getting started locally", html);
        Assert.Contains("Write your own", html);
    }

    [Fact]
    public async Task WithNoTemplatesSeeded_NoShortcutMenuIsRendered()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.DoesNotContain("Start from", html);
        // The button itself is not conditional on having templates — a blank draft is always allowed.
        Assert.Contains("Email candidates", html);
    }
}
