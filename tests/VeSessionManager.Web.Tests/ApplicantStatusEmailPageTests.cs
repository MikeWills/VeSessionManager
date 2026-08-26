using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The bulk-email screen off Applicant Status (2026-08-26) — composing one message and sending it to
/// some or all of a team's candidates still waiting on an FCC grant.
///
/// <para>Same reasoning as <c>CandidateEmailPageTests</c>: both the team id and the candidate ids
/// arrive from the request, and the message is sent from that team's own SMTP — so the authorization
/// tests are the point. Each test builds its own factory, since the seeded database is shared
/// per-factory and these mutate it.</para>
/// </summary>
public class ApplicantStatusEmailPageTests
{
    private static string Url(int teamId) => $"/SessionManager/ApplicantStatusEmail?teamId={teamId}";

    private static async Task ConfigureTeamEmailAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamId = factory.Seeded.TeamId;

        var team = await db.Teams.FirstAsync(t => t.Id == teamId);
        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";

        db.EmailSettings.Add(new EmailSettings
        {
            TeamId = teamId,
            FromAddress = "noreply@example.org",
            FromDisplayName = "Test Team",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedPendingCandidateAsync(WebAppFactory factory, int sessionId, string name, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidate = new Candidate
        {
            SessionId = sessionId, Name = name, FirstName = name.Split(' ')[0], Email = email,
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-14), Tested = true,
            ApplicationStatus = CandidateApplicationStatus.Received
        };
        db.Candidates.Add(candidate);
        await db.SaveChangesAsync();
        return candidate.Id;
    }

    private static async Task<(int TeamId, int SessionId)> SeedOtherTeamWithPendingSessionAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var team = new Team { Name = "OTHER-TEAM", ExamToolsTeamCode = "OTHER" };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        var session = new Session
        {
            TeamId = team.Id, VecId = factory.Seeded.VecId,
            FeeConfigurationId = (await db.FeeConfigurations.FirstAsync()).Id,
            ExamToolsSessionId = "et-other-team-session", Title = "Another team's session",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(-3), DurationMinutes = 60, Status = SessionStatus.Active
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return (team.Id, session.Id);
    }

    private static async Task DemoteSeededUserToTeamAdminAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == factory.Seeded.UserId);
        user.Role = UserRole.TeamAdmin;
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(
        HttpClient client, string url, IEnumerable<int> candidateIds, int teamId,
        string subject = "Hi", string body = "<p>Hi</p>")
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Subject", subject),
            new("Body", body),
            new("message", "0"),
            new("teamId", teamId.ToString()),
            new("__RequestVerificationToken", token)
        };
        fields.AddRange(candidateIds.Select(id => new KeyValuePair<string, string>("SelectedCandidateIds", id.ToString())));
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task TeamLead_IsForbidden()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.TeamLead);

        var response = await client.GetAsync(Url(factory.Seeded.TeamId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SessionManager_CanReachThePage()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync(Url(factory.Seeded.TeamId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoTeamIdAtAll_RedirectsToApplicantStatus()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await client.GetAsync("/SessionManager/ApplicantStatusEmail");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/SessionManager/ApplicantStatus", response.Headers.Location!.ToString());
    }

    /// <summary>
    /// #238's own guard, in a new place: a TeamAdmin locked to their own team must not reach a
    /// different team's candidates just by editing the query string, the same way the session-scoped
    /// screen refuses a foreign session id.
    /// </summary>
    [Fact]
    public async Task ATeamAdminRequestingAnotherTeam_IsForbidden()
    {
        using var factory = new WebAppFactory();
        await DemoteSeededUserToTeamAdminAsync(factory);
        var (otherTeamId, _) = await SeedOtherTeamWithPendingSessionAsync(factory);
        using var client = factory.CreateClientAs(UserRole.TeamAdmin);

        var response = await client.GetAsync(Url(otherTeamId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostingASend_MailsTheSelectedPendingCandidate_AndRedirectsToApplicantStatus()
    {
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var candidateId = await SeedPendingCandidateAsync(factory, factory.Seeded.SessionId, "Ana Ruiz", "ana@example.com");
        using var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostWithTokenAsync(client, Url(factory.Seeded.TeamId), [candidateId], factory.Seeded.TeamId,
            subject: "An update on your application", body: "<p>The FCC has a known delay.</p>");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/SessionManager/ApplicantStatus", response.Headers.Location!.ToString());
        Assert.Single(factory.SentEmails);
        Assert.Equal("ana@example.com", factory.SentEmails[0].ToAddress);
    }

    /// <summary>A posted id the page never offered — a candidate on a different team — must not become a recipient.</summary>
    [Fact]
    public async Task PostingASend_WithACandidateFromAnotherTeam_IsForbidden()
    {
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var mine = await SeedPendingCandidateAsync(factory, factory.Seeded.SessionId, "Ana Ruiz", "ana@example.com");
        var (_, otherSessionId) = await SeedOtherTeamWithPendingSessionAsync(factory);
        var theirs = await SeedPendingCandidateAsync(factory, otherSessionId, "Not Mine", "elsewhere@example.com");
        using var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostWithTokenAsync(client, Url(factory.Seeded.TeamId), [mine, theirs], factory.Seeded.TeamId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.SentEmails);
    }
}
