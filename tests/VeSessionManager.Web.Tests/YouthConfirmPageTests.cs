using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The public youth-rate confirmation page's COPPA declaration (2026-08-26) — a dropdown asking
/// whether the candidate is under 13, and, only when the answer is yes, a required checkbox
/// confirming the parental-consent form was already sent to ExamTools. Testing cannot legally
/// proceed without that form on file, so answering "yes" without checking the box must not let the
/// candidate through.
///
/// <para>Every seeded team here is deliberately left with no Square credentials, so a valid POST
/// resolves to <c>SquareNotConfigured</c> rather than attempting a real Square call — the COPPA
/// declaration is recorded before that check runs, so this still exercises the behavior these tests
/// are actually about.</para>
/// </summary>
public class YouthConfirmPageTests
{
    private static string Url(Guid token) => $"/youth-confirm/{token}";

    private static async Task<(Guid Token, int CandidateId)> SeedUnpaidYouthEligibleCandidateAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var vec = new Vec { Name = "ARRL-" + Guid.NewGuid(), SupportsYouthProgram = true };
        var user = new User { Name = "System", Email = $"system-{Guid.NewGuid()}@localhost", Role = UserRole.SystemAdmin };
        db.Vecs.Add(vec);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var team = new Team { Name = "YOUTH-TEST-" + Guid.NewGuid(), CreatedUtc = DateTime.UtcNow };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        var feeConfiguration = new FeeConfiguration
        {
            VecId = vec.Id, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, YouthExamFeeAmount = 5m,
            CreatedByUserId = user.Id, CreatedUtc = DateTime.UtcNow
        };
        db.FeeConfigurations.Add(feeConfiguration);
        await db.SaveChangesAsync();

        var session = new Session
        {
            ExamToolsSessionId = "et-youth-" + Guid.NewGuid(), Title = "Youth Test Session",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(3), DurationMinutes = 60,
            TeamId = team.Id, VecId = vec.Id, FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active, CreatedUtc = DateTime.UtcNow
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var candidate = new Candidate
        {
            SessionId = session.Id, ExamToolsApplicantId = Guid.NewGuid().ToString(),
            Name = "Young Candidate", Email = "young@example.com", DateRegisteredUtc = DateTime.UtcNow
        };
        db.Candidates.Add(candidate);
        await db.SaveChangesAsync();

        var token = Guid.NewGuid();
        db.Payments.Add(new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Unpaid, YouthConfirmationToken = token, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (token, candidate.Id);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    [Fact]
    public async Task GetRequest_RendersTheSharedDefaultIntroText()
    {
        using var factory = new WebAppFactory();
        var (token, _) = await SeedUnpaidYouthEligibleCandidateAsync(factory);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(Url(token));

        Assert.Contains("After you pass, you may be able to claim the FCC license fee back", html);
    }

    [Fact]
    public async Task PostingUnder13_WithoutTheCoppaCheckbox_IsRejected_CandidateUntouched()
    {
        using var factory = new WebAppFactory();
        var (token, candidateId) = await SeedUnpaidYouthEligibleCandidateAsync(factory);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryTokenAsync(client, Url(token));

        var response = await client.PostAsync(Url(token), new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
            new KeyValuePair<string, string>("Input.ConfirmYouth", "true"),
            new KeyValuePair<string, string>("Input.DeclaredUnder13", "true")
            // Input.CoppaFormSent deliberately omitted.
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered with a validation error, not redirected
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("COPPA consent form has been sent", html);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.Candidates.SingleAsync(c => c.Id == candidateId);
        Assert.Null(candidate.DeclaredUnder13);
        Assert.Null(candidate.CoppaFormSentConfirmedUtc);
    }

    [Fact]
    public async Task PostingWithNoAnswerToTheAgeQuestion_IsRejected()
    {
        using var factory = new WebAppFactory();
        var (token, _) = await SeedUnpaidYouthEligibleCandidateAsync(factory);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryTokenAsync(client, Url(token));

        var response = await client.PostAsync(Url(token), new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
            new KeyValuePair<string, string>("Input.ConfirmYouth", "true")
            // Input.DeclaredUnder13 deliberately omitted (the "Select one" option posts "").
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Please answer whether the candidate is under 13", html);
    }

    [Fact]
    public async Task PostingUnder13_WithTheCoppaCheckboxChecked_RecordsBothFields()
    {
        using var factory = new WebAppFactory();
        var (token, candidateId) = await SeedUnpaidYouthEligibleCandidateAsync(factory);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryTokenAsync(client, Url(token));

        var response = await client.PostAsync(Url(token), new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
            new KeyValuePair<string, string>("Input.ConfirmYouth", "true"),
            new KeyValuePair<string, string>("Input.DeclaredUnder13", "true"),
            new KeyValuePair<string, string>("Input.CoppaFormSent", "true")
        ]));

        // Team has no Square credentials, so this resolves to SquareNotConfigured (re-rendered),
        // not a redirect — but the declaration itself is recorded before that check runs.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.Candidates.SingleAsync(c => c.Id == candidateId);
        Assert.True(candidate.DeclaredUnder13);
        Assert.NotNull(candidate.CoppaFormSentConfirmedUtc);
    }

    [Fact]
    public async Task PostingNotUnder13_DoesNotRequireTheCoppaCheckbox()
    {
        using var factory = new WebAppFactory();
        var (token, candidateId) = await SeedUnpaidYouthEligibleCandidateAsync(factory);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryTokenAsync(client, Url(token));

        var response = await client.PostAsync(Url(token), new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
            new KeyValuePair<string, string>("Input.ConfirmYouth", "true"),
            new KeyValuePair<string, string>("Input.DeclaredUnder13", "false")
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("COPPA consent form has been sent", html);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.Candidates.SingleAsync(c => c.Id == candidateId);
        Assert.False(candidate.DeclaredUnder13);
        Assert.Null(candidate.CoppaFormSentConfirmedUtc);
    }
}
