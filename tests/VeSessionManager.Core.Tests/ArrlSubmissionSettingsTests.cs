using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Saving a team's ARRL submission settings (issue #197).
///
/// <para>Validation lives in the service rather than on the page, per the rule established by #275:
/// a service guard covers every future caller, not only the one screen that exists today.</para>
/// </summary>
public class ArrlSubmissionSettingsTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TeamSettingsService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now), NullLogger<TeamSettingsService>.Instance);

    private static async Task<(AppDbContext Db, TeamSettingsService Service, Team Team, User User)> SeedAsync()
    {
        var db = CreateContext();
        var user = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        var team = new Team { Name = "MARC", CreatedUtc = Now };
        db.Users.Add(user);
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return (db, CreateService(db), team, user);
    }

    [Fact]
    public async Task AValidUpdate_IsStoredAndAudited()
    {
        var (db, service, team, user) = await SeedAsync();

        var result = await service.UpdateArrlSubmissionAsync(
            team.Id, "/Nick Booth (CC)/HRCC VE Team", ArrlSubmissionEmailSource.SessionLead, null,
            "Remote Online", ArrlPaymentMethod.CreditCardOnFile, "Bill the card on file", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);

        var saved = await db.Teams.SingleAsync();
        Assert.Equal("/Nick Booth (CC)/HRCC VE Team", saved.ArrlSubmissionNamePostfix);
        Assert.Equal(ArrlSubmissionEmailSource.SessionLead, saved.ArrlSubmissionEmailSource);
        Assert.Equal("Remote Online", saved.ArrlSubmissionLocation);
        Assert.Equal(ArrlPaymentMethod.CreditCardOnFile, saved.ArrlSubmissionPaymentMethod);
        Assert.Equal("Bill the card on file", saved.ArrlSubmissionNote);
        Assert.True(saved.IsArrlSubmissionConfigured);

        Assert.Contains(db.AuditLogs, a => a.Action == "TeamArrlSubmissionUpdated");
    }

    /// <summary>
    /// The one cross-field rule: an address is required when, and only when, the source is
    /// <see cref="ArrlSubmissionEmailSource.TeamAddress"/>. Rejected rather than silently saved,
    /// because a half-configured team would otherwise reach the preview with a blank required field
    /// and no explanation of which setting caused it.
    /// </summary>
    [Fact]
    public async Task TeamAddressWithNoAddress_IsRejected()
    {
        var (db, service, team, user) = await SeedAsync();

        var result = await service.UpdateArrlSubmissionAsync(
            team.Id, null, ArrlSubmissionEmailSource.TeamAddress, "   ",
            "Remote Online", ArrlPaymentMethod.CreditCardOnFile, null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.ArrlSubmissionEmailRequired, result);
        Assert.Null((await db.Teams.SingleAsync()).ArrlSubmissionLocation);
    }

    /// <summary>Blank is a complete answer for both optional fields — MARC files with an empty postfix and empty Notes.</summary>
    [Fact]
    public async Task BlanksAreStoredAsNull_NotEmptyStrings()
    {
        var (db, service, team, user) = await SeedAsync();

        await service.UpdateArrlSubmissionAsync(
            team.Id, "   ", ArrlSubmissionEmailSource.SessionLead, null,
            "Remote Online", ArrlPaymentMethod.CreditCardOnFile, "  ", user.Id, CancellationToken.None);

        var saved = await db.Teams.SingleAsync();
        Assert.Null(saved.ArrlSubmissionNamePostfix);
        Assert.Null(saved.ArrlSubmissionNote);
        Assert.True(saved.IsArrlSubmissionConfigured);
    }

    /// <summary>
    /// Whitespace is preserved inside the postfix, only trimmed at the ends. HRCC's real value starts
    /// with a slash and no space, and this app must not decide otherwise for them.
    /// </summary>
    [Fact]
    public async Task ThePostfixKeepsItsInternalPunctuation()
    {
        var (db, service, team, user) = await SeedAsync();

        await service.UpdateArrlSubmissionAsync(
            team.Id, "/Nick Booth (CC)/HRCC VE Team", ArrlSubmissionEmailSource.SessionLead, null,
            "Remote Online", ArrlPaymentMethod.CreditCardOnFile, null, user.Id, CancellationToken.None);

        Assert.Equal("/Nick Booth (CC)/HRCC VE Team", (await db.Teams.SingleAsync()).ArrlSubmissionNamePostfix);
    }

    [Fact]
    public async Task ALocationIsRequired()
    {
        var (db, service, team, user) = await SeedAsync();

        var result = await service.UpdateArrlSubmissionAsync(
            team.Id, null, ArrlSubmissionEmailSource.SessionLead, null,
            " ", ArrlPaymentMethod.CreditCardOnFile, null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.ArrlSubmissionLocationRequired, result);
    }

    [Fact]
    public async Task AnUnknownTeam_IsNotFound()
    {
        var (_, service, _, user) = await SeedAsync();

        var result = await service.UpdateArrlSubmissionAsync(
            9999, null, ArrlSubmissionEmailSource.SessionLead, null,
            "Remote Online", ArrlPaymentMethod.CreditCardOnFile, null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
    }
}
