using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Surfacing a filing ARRL never confirmed (issue #197).
///
/// <para><b>Exactly the class of problem the bell exists for.</b> An unconfirmed submission leaves
/// the session looking unsubmitted — which is correct, since it may or may not have been filed — but
/// nothing else anywhere would make somebody go and look. It cannot be retried and must not be
/// resent, so the only resolution is a person telephoning ARRL, and the only thing that prompts that
/// is this alert.</para>
/// </summary>
public class ArrlUnconfirmedAlertTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(AppDbContext Db, Session Session, Team Team)> SeedAsync(
        ArrlReceiptOutcome outcome = ArrlReceiptOutcome.Unknown,
        VecSubmissionStatus sessionStatus = VecSubmissionStatus.NotSubmitted,
        string? transportError = null)
    {
        var db = CreateContext();
        var team = new Team { Name = "MARC", CreatedUtc = Now };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "SM", Role = UserRole.SessionManager };
        var fee = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1), FeeCollectionEnabled = true,
            ExamFeeAmount = 15m, RetainedAmount = 7m, CreatedUtc = Now, CreatedByUser = user
        };
        var session = new Session
        {
            ExamToolsSessionId = "et-1", Title = "Testing", Team = team, Vec = vec, FeeConfiguration = fee,
            ScheduledStartUtc = Now.AddDays(-2), DurationMinutes = 120, CreatedUtc = Now,
            VecSubmissionStatus = sessionStatus
        };
        db.AddRange(team, vec, user, fee, session);
        await db.SaveChangesAsync();

        db.ArrlVecSubmissions.Add(new ArrlVecSubmission
        {
            SessionId = session.Id, TeamId = team.Id, SubmittedByUserId = user.Id, SubmittedUtc = Now.AddHours(-3),
            FullName = "Mike Wills", CallSign = "WX0MIK", Email = "a@b.c", Phone = "1",
            SessionDate = "2026-08-16", Location = "Remote Online",
            PaymentMethod = ArrlPaymentMethod.CreditCardOnFile, AmountCharged = "8.00",
            ArchiveFileName = "archive.zip", Outcome = outcome,
            UnconfirmedFileNames = outcome == ArrlReceiptOutcome.Unknown ? "archive.zip" : null,
            TransportError = transportError
        });
        await db.SaveChangesAsync();

        return (db, session, team);
    }

    private static Task<AlertFeed> FeedAsync(AppDbContext db, UserRole role, IReadOnlyList<int>? teamIds) =>
        new AlertFeedService(db).GetAsync(role, teamIds, CancellationToken.None);

    /// <summary>
    /// The gate is per source. Reconciliation is admin-only, but this alert points at session detail,
    /// which every role can open — and a single gate at the top would have hidden it from exactly the
    /// people who press the button.
    /// </summary>
    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task EveryRoleSeesIt(UserRole role)
    {
        var (db, _, team) = await SeedAsync();

        var feed = await FeedAsync(db, role, [team.Id]);

        var item = Assert.Single(feed.Items);
        Assert.Equal("ARRL submission", item.Category);
    }

    /// <summary>The link has to land on the session, not on a list — so the alert carries the session's own route id.</summary>
    [Fact]
    public async Task ItLinksToTheSessionItself()
    {
        var (db, session, team) = await SeedAsync();

        var feed = await FeedAsync(db, UserRole.SessionManager, [team.Id]);

        var item = Assert.Single(feed.Items);
        Assert.Equal("/SessionManager/Detail", item.PageName);
        Assert.Equal(session.Id, item.RouteId);
    }

    /// <summary>
    /// It says what to do, and specifically does <b>not</b> say the filing failed. Telling someone it
    /// failed is what produces a duplicate submission ARRL cannot undo.
    /// </summary>
    [Fact]
    public async Task ItSaysItMayHaveBeenFiledRatherThanThatItFailed()
    {
        var (db, _, team) = await SeedAsync();

        var item = Assert.Single((await FeedAsync(db, UserRole.SessionManager, [team.Id])).Items);

        Assert.Contains("may still have been filed", item.Detail);
        Assert.DoesNotContain("failed", item.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATransportFailureSaysWhatWentWrong()
    {
        var (db, _, team) = await SeedAsync(transportError: "connection reset");

        var item = Assert.Single((await FeedAsync(db, UserRole.SessionManager, [team.Id])).Items);

        Assert.Contains("connection reset", item.Detail);
    }

    [Fact]
    public async Task AConfirmedSubmissionRaisesNothing()
    {
        var (db, _, team) = await SeedAsync(ArrlReceiptOutcome.Succeeded, VecSubmissionStatus.Submitted);

        Assert.Empty((await FeedAsync(db, UserRole.SessionManager, [team.Id])).Items);
    }

    /// <summary>
    /// The alert clears when a human marks the session submitted, which is what they do once ARRL
    /// confirms by phone. There is no separate "resolved" flag because that action already means it.
    /// </summary>
    [Fact]
    public async Task MarkingTheSessionSubmittedClearsIt()
    {
        var (db, session, team) = await SeedAsync();
        session.VecSubmissionStatus = VecSubmissionStatus.Submitted;
        await db.SaveChangesAsync();

        Assert.Empty((await FeedAsync(db, UserRole.SessionManager, [team.Id])).Items);
    }

    [Fact]
    public async Task AnotherTeamsSubmissionIsNotShown()
    {
        var (db, _, _) = await SeedAsync();

        Assert.Empty((await FeedAsync(db, UserRole.SessionManager, [9999])).Items);
    }

    /// <summary>Null teamIds is SystemAdmin's "every team", the same semantics NavBadgeCountService uses.</summary>
    [Fact]
    public async Task ASystemAdminSeesEveryTeams()
    {
        var (db, _, _) = await SeedAsync();

        Assert.Single((await FeedAsync(db, UserRole.SystemAdmin, null)).Items);
    }
}
