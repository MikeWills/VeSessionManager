using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The Sessions chip has to land on a list that actually contains what it counted (#423).
///
/// <para>The badge has no date bound; the Sessions page defaults to "Last 7 + Upcoming". A chip
/// reading 2 could therefore send you to a list of 1, and the honest number looked like the broken
/// one. Asserted against rendered markup because the whole failure lives in the link's query string —
/// no service test can see it.</para>
/// </summary>
public class SessionsPendingVecSubmissionLinkTests
{
    private static async Task<int> SeedPendingSubmissionSessionAsync(WebAppFactory factory, DateTime startUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vec = await db.Vecs.FirstAsync();
        var fee = await db.FeeConfigurations.FirstAsync();

        var session = new Session
        {
            TeamId = factory.Seeded.TeamId,
            VecId = vec.Id,
            FeeConfigurationId = fee.Id,
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Pending submission",
            ExtId = "PENDING-SUBMIT-EXT",
            ScheduledStartUtc = startUtc,
            DurationMinutes = 60,
            CreatedUtc = DateTime.UtcNow
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.Candidates.Add(new Candidate
        {
            SessionId = session.Id,
            Name = "Graded Candidate",
            Email = "graded@example.org",
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-40),
            ApplicationStatus = CandidateApplicationStatus.Granted
        });
        await db.SaveChangesAsync();
        return session.Id;
    }

    [Fact]
    public async Task TheChipLinksToAViewThatContainsWhatItCounted()
    {
        using var factory = new WebAppFactory();
        // Well outside the page's default "Last 7 + Upcoming" window.
        await SeedPendingSubmissionSessionAsync(factory, DateTime.UtcNow.AddDays(-30));
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Matches("status=PendingVecSubmission", html);
        Assert.Matches("applied=[Tt]rue", html);
    }

    /// <summary>
    /// Following that link must actually show the session — the point of the change, and the part a
    /// markup assertion alone would not prove.
    /// </summary>
    [Fact]
    public async Task FollowingTheLink_ShowsASessionOlderThanTheDefaultWindow()
    {
        using var factory = new WebAppFactory();
        await SeedPendingSubmissionSessionAsync(factory, DateTime.UtcNow.AddDays(-30));
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var defaultView = await client.GetStringAsync("/SessionManager/Index");
        var linked = await client.GetStringAsync("/SessionManager/Index?applied=true&status=PendingVecSubmission&dateRange=");

        // The list renders ExtId in its "Session ID" column, not Title.
        Assert.DoesNotContain("PENDING-SUBMIT-EXT", defaultView);
        Assert.Contains("PENDING-SUBMIT-EXT", linked);
    }

    /// <summary>A withdrawal is settled but produces no paperwork, so it must not put a session on this list at all.</summary>
    [Fact]
    public async Task AWithdrawalOnlySession_IsNotPendingSubmission()
    {
        using var factory = new WebAppFactory();
        var sessionId = await SeedPendingSubmissionSessionAsync(factory, DateTime.UtcNow.AddDays(-30));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = await db.Candidates.FirstAsync(c => c.SessionId == sessionId);
            candidate.ApplicationStatus = CandidateApplicationStatus.NotTested;
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var linked = await client.GetStringAsync("/SessionManager/Index?applied=true&status=PendingVecSubmission&dateRange=");

        Assert.DoesNotContain("PENDING-SUBMIT-EXT", linked);
    }
}
