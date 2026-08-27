using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The youth/COPPA declarations from the public confirmation form, shown to VEs (2026-08-27) — a
/// "Youth" (and, when applicable, "COPPA form sent") tag on the session roster, and a fuller "Youth
/// rate" line on candidate detail. What was stored but invisible before this: a VE about to test an
/// under-13 candidate had no way to see whether the parental-consent form was on file without a
/// database query.
/// </summary>
public class YouthCoppaDisplayTests
{
    private static async Task MarkYouthConfirmedAsync(WebAppFactory factory, bool under13, bool coppaSent)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await db.Candidates.FirstAsync(c => c.Id == factory.Seeded.CandidateId);
        candidate.DeclaredUnder13 = under13;
        candidate.CoppaFormSentConfirmedUtc = coppaSent ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Roster_YouthConfirmedUnder13WithCoppa_ShowsBothTags()
    {
        using var factory = new WebAppFactory();
        await MarkYouthConfirmedAsync(factory, under13: true, coppaSent: true);
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.Contains(">Youth</span>", html);
        Assert.Contains(">COPPA form sent</span>", html);
    }

    [Fact]
    public async Task Roster_YouthConfirmedNotUnder13_ShowsYouthTagOnly()
    {
        using var factory = new WebAppFactory();
        await MarkYouthConfirmedAsync(factory, under13: false, coppaSent: false);
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.Contains(">Youth</span>", html);
        Assert.DoesNotContain(">COPPA form sent</span>", html);
    }

    [Fact]
    public async Task Roster_NoYouthConfirmation_ShowsNeitherTag()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/Detail/{factory.Seeded.SessionId}");

        Assert.DoesNotContain(">Youth</span>", html);
        Assert.DoesNotContain(">COPPA form sent</span>", html);
    }

    [Fact]
    public async Task CandidateDetail_Under13WithCoppa_ShowsTheFullLine()
    {
        using var factory = new WebAppFactory();
        await MarkYouthConfirmedAsync(factory, under13: true, coppaSent: true);
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/CandidateDetail/{factory.Seeded.CandidateId}");

        Assert.Contains("Youth rate", html);
        Assert.Contains("under 13: Yes", html);
        Assert.Contains("COPPA form sent to ExamTools (confirmed", html);
    }

    [Fact]
    public async Task CandidateDetail_NotUnder13_SaysNoAndNeverMentionsCoppa()
    {
        using var factory = new WebAppFactory();
        await MarkYouthConfirmedAsync(factory, under13: false, coppaSent: false);
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/CandidateDetail/{factory.Seeded.CandidateId}");

        Assert.Contains("under 13: No", html);
        Assert.DoesNotContain("COPPA form sent to ExamTools", html);
    }

    [Fact]
    public async Task CandidateDetail_NoYouthConfirmation_OmitsTheLineEntirely()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/CandidateDetail/{factory.Seeded.CandidateId}");

        Assert.DoesNotContain("Youth rate", html);
    }

    /// <summary>PII purge clears the under-13 answer but deliberately retains the COPPA timestamp — the compliance record must stay visible on its own.</summary>
    [Fact]
    public async Task CandidateDetail_AfterPiiPurge_CoppaRecordStillShowsAlone()
    {
        using var factory = new WebAppFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = await db.Candidates.FirstAsync(c => c.Id == factory.Seeded.CandidateId);
            // The shape PII purge leaves: DeclaredUnder13 nulled, the compliance timestamp kept.
            candidate.DeclaredUnder13 = null;
            candidate.CoppaFormSentConfirmedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync($"/SessionManager/CandidateDetail/{factory.Seeded.CandidateId}");

        Assert.Contains("Youth rate", html);
        Assert.Contains("COPPA form sent to ExamTools (confirmed", html);
        Assert.DoesNotContain("under 13:", html);
    }
}
