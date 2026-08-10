using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// "Is this session finished?" has to be written more than once, and this is what stops the copies
/// disagreeing.
///
/// <para><b>Why it cannot simply be shared.</b> <see cref="Session.CompletedUtc"/> is the one
/// definition, but EF Core cannot translate a C# property into SQL, so any query that filters or
/// sorts on completion must spell the rule out again as
/// <c>TestingCompletedUtc != null || ExamToolsClosedUtc != null</c>. The language offers no way to
/// make those two the same code without a predicate-rewriting dependency, so a test does the job
/// instead: it runs both spellings over the same rows and asserts they select the same sessions.</para>
///
/// <para><b>Why it is worth a test at all.</b> The rule this replaces —
/// <c>Status == SessionStatus.Active</c> — reads like "currently running" and actually means "not
/// cancelled", since Status never leaves Active except on cancellation. That misreading has shipped
/// twice: it made VolunteerExaminerSyncService re-poll a team's entire history hourly for months,
/// and then reappeared in the VE Roster's "sessions worked" count, where a VE rostered onto a
/// *future* session already had it counted. Both looked correct on every screen.</para>
/// </summary>
public class SessionCompletionRuleTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Every combination of the two timestamps, plus a cancelled row for good measure.</summary>
    private static async Task<AppDbContext> SeedMatrixAsync()
    {
        var dbContext = CreateContext();
        var vec = new Vec { Name = "ARRL" };
        var team = new Team { Name = "Test Team" };
        var fee = new FeeConfiguration { Vec = vec, EffectiveDate = Now.AddYears(-1), ExamFeeAmount = 15m, RetainedAmount = 7m };

        Session Make(string id, DateTime? tested, DateTime? closed, SessionStatus status = SessionStatus.Active) => new()
        {
            ExamToolsSessionId = id,
            Title = id,
            ScheduledStartUtc = Now.AddDays(-1),
            DurationMinutes = 120,
            Vec = vec,
            Team = team,
            FeeConfiguration = fee,
            TestingCompletedUtc = tested,
            ExamToolsClosedUtc = closed,
            Status = status
        };

        dbContext.Sessions.AddRange(
            Make("neither", null, null),
            Make("tested-only", Now.AddHours(-2), null),
            Make("closed-only", null, Now.AddHours(-1)),
            Make("both", Now.AddHours(-2), Now.AddHours(-1)),
            Make("cancelled-neither", null, null, SessionStatus.Cancelled),
            Make("cancelled-closed", null, Now.AddHours(-1), SessionStatus.Cancelled));
        await dbContext.SaveChangesAsync();
        return dbContext;
    }

    /// <summary>
    /// The query spelling (what EF translates) and the entity property (what pages read) must select
    /// exactly the same sessions. If someone "simplifies" either one, this fails.
    /// </summary>
    [Fact]
    public async Task QuerySpelling_AndEntityProperty_SelectTheSameSessions()
    {
        await using var dbContext = await SeedMatrixAsync();

        var fromQuery = await dbContext.Sessions
            .Where(s => s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null)
            .Select(s => s.ExamToolsSessionId)
            .OrderBy(id => id)
            .ToListAsync();

        var fromProperty = (await dbContext.Sessions.ToListAsync())
            .Where(s => s.IsCompleted)
            .Select(s => s.ExamToolsSessionId)
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(fromQuery, fromProperty);
        Assert.Equal(["both", "cancelled-closed", "closed-only", "tested-only"], fromQuery);
    }

    /// <summary>
    /// Cancellation and completion are independent axes. A cancelled session that ExamTools also
    /// closed is still "completed" by this rule — the Status chip resolves the conflict by checking
    /// Cancelled first, which is presentation, not this rule's job.
    /// </summary>
    [Fact]
    public async Task CompletionIsIndependentOfStatus()
    {
        await using var dbContext = await SeedMatrixAsync();
        var sessions = await dbContext.Sessions.ToListAsync();

        Assert.True(sessions.Single(s => s.ExamToolsSessionId == "cancelled-closed").IsCompleted);
        Assert.False(sessions.Single(s => s.ExamToolsSessionId == "cancelled-neither").IsCompleted);

        // The trap this whole rule exists to avoid: Status == Active selects everything ever run,
        // including sessions that finished, so it is never a completion test. Asserted as a
        // disagreement between the two *sets*, not their sizes — the first version of this test
        // compared counts, which happened to be equal (4 and 4) for two entirely different sets.
        var active = sessions.Where(s => s.Status == SessionStatus.Active).Select(s => s.ExamToolsSessionId).ToHashSet();
        var completed = sessions.Where(s => s.IsCompleted).Select(s => s.ExamToolsSessionId).ToHashSet();

        // Active but not finished — so Active cannot mean "running".
        Assert.Contains("neither", active);
        Assert.DoesNotContain("neither", completed);
        // Finished but not Active — so the two axes are genuinely independent.
        Assert.Contains("cancelled-closed", completed);
        Assert.DoesNotContain("cancelled-closed", active);
        // Active *and* finished: the case that made every old session read as live.
        Assert.Contains("closed-only", active);
        Assert.Contains("closed-only", completed);
    }

    /// <summary>
    /// CompletedUtc prefers the manual timestamp when both exist — a Session Manager marking the
    /// session is the more specific fact than ExamTools observing it closed. Session Detail renders
    /// this date, so the preference is user-visible.
    /// </summary>
    [Fact]
    public async Task CompletedUtc_PrefersTheManualTimestampOverExamToolsClosing()
    {
        await using var dbContext = await SeedMatrixAsync();
        var sessions = await dbContext.Sessions.ToListAsync();

        Assert.Equal(Now.AddHours(-2), sessions.Single(s => s.ExamToolsSessionId == "both").CompletedUtc);
        Assert.Equal(Now.AddHours(-1), sessions.Single(s => s.ExamToolsSessionId == "closed-only").CompletedUtc);
        Assert.Null(sessions.Single(s => s.ExamToolsSessionId == "neither").CompletedUtc);
    }
}
