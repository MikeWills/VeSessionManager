using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #440 — recording and clearing the skip. These are the rules that keep the alert honest: an
/// alert nobody can trust to disappear is one people learn to ignore, which is where the reconciliation
/// badge started.
/// </summary>
public class SkippedSessionRecordingTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 2, 5, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SkippedSession Existing(int teamId, string sessionId = "remote-1", DateTime? lastSeen = null) => new()
    {
        TeamId = teamId,
        ExamToolsSessionId = sessionId,
        VecCode = "arrl",
        Reason = SkippedSessionReason.NoMatchingVec,
        FirstSeenUtc = Now,
        LastSeenUtc = lastSeen ?? Now
    };

    [Fact]
    public async Task ANewSkip_IsRecorded()
    {
        await using var dbContext = CreateContext();

        await SkippedSessionTracker.RecordAsync(dbContext, teamId: 1, "remote-1", "arrl",
            "W9NB Tuesday", new DateTime(2026, 8, 19), SkippedSessionReason.NoMatchingVec, Now, CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var skip = Assert.Single(dbContext.SkippedSessions);
        Assert.Equal("arrl", skip.VecCode);
        Assert.Equal(Now, skip.FirstSeenUtc);
        Assert.Equal(Now, skip.LastSeenUtc);
    }

    /// <summary>
    /// Re-stamped, not duplicated — ingestion runs hourly, so inserting per poll would make one
    /// misconfiguration read as hundreds of problems within a day.
    /// </summary>
    [Fact]
    public async Task ASkipSeenAgain_IsReStamped_NotDuplicated()
    {
        await using var dbContext = CreateContext();
        dbContext.SkippedSessions.Add(Existing(teamId: 1));
        await dbContext.SaveChangesAsync();
        var later = Now.AddDays(5);

        await SkippedSessionTracker.RecordAsync(dbContext, teamId: 1, "remote-1", "arrl",
            null, null, SkippedSessionReason.NoMatchingVec, later, CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var skip = Assert.Single(dbContext.SkippedSessions);
        Assert.Equal(Now, skip.FirstSeenUtc);    // still says how long it has been broken
        Assert.Equal(later, skip.LastSeenUtc);
    }

    /// <summary>Fixing the configuration is the resolution — the alert must clear itself the moment the session ingests.</summary>
    [Fact]
    public async Task WhenTheSessionFinallyIngests_TheSkipIsCleared()
    {
        await using var dbContext = CreateContext();
        dbContext.SkippedSessions.Add(Existing(teamId: 1));
        await dbContext.SaveChangesAsync();

        await SkippedSessionTracker.ClearAsync(dbContext, teamId: 1, "remote-1", CancellationToken.None);
        await dbContext.SaveChangesAsync();

        Assert.Empty(dbContext.SkippedSessions);
    }

    /// <summary>
    /// A session the feed has stopped reporting — cancelled, or moved out of the ingest window — is no
    /// longer a configuration fault, and an alert about it would be permanently unresolvable. Anything
    /// not re-stamped by the run that just finished is swept.
    /// </summary>
    [Fact]
    public async Task ASkipTheFeedNoLongerReports_IsSweptAway()
    {
        await using var dbContext = CreateContext();
        dbContext.SkippedSessions.Add(Existing(teamId: 1, "still-skipped", lastSeen: Now));
        dbContext.SkippedSessions.Add(Existing(teamId: 1, "gone-from-feed", lastSeen: Now.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        await SkippedSessionTracker.SweepAsync(dbContext, teamId: 1, runStartedUtc: Now, CancellationToken.None);
        await dbContext.SaveChangesAsync();

        Assert.Equal("still-skipped", Assert.Single(dbContext.SkippedSessions).ExamToolsSessionId);
    }

    /// <summary>
    /// ⚠️ The sweep is per team. Ingestion runs team by team, so one team's run says nothing about
    /// whether another team's skips are still current — sweeping globally would clear a live fault on
    /// every other team every hour, and the alert would flicker instead of persisting.
    /// </summary>
    [Fact]
    public async Task TheSweep_LeavesOtherTeamsAlone()
    {
        await using var dbContext = CreateContext();
        dbContext.SkippedSessions.Add(Existing(teamId: 2, "other-team", lastSeen: Now.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        await SkippedSessionTracker.SweepAsync(dbContext, teamId: 1, runStartedUtc: Now, CancellationToken.None);
        await dbContext.SaveChangesAsync();

        Assert.Single(dbContext.SkippedSessions);
    }
}
