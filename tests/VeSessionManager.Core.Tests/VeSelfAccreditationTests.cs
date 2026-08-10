using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// VEs maintaining their own VEC accreditations (requested 2026-08-10), which reversed the original
/// decision that accreditations belonged to the team.
///
/// <para>The case that earns a test of its own is the authorization one: an accreditation id is a
/// number in a form, and self-service is reached by anyone holding a sign-in link.</para>
/// </summary>
public class VeSelfAccreditationTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerManagementService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<(VolunteerExaminer Person, Vec Vec)> SeedAsync(AppDbContext dbContext, string callSign = "N2SPG")
    {
        var person = new VolunteerExaminer { Name = "Sam Granger", CallSign = callSign };
        var vec = new Vec { Name = "ARRL " + callSign };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        return (person, vec);
    }

    /// <summary>
    /// The boundary. Without the ownership check, a signed-in VE could delete another VE's
    /// accreditation by changing the number in the form — self-service is reachable by anyone
    /// holding a sign-in link, so this is the whole protection.
    /// </summary>
    [Fact]
    public async Task AVeCannotRemoveSomeoneElsesAccreditation()
    {
        await using var dbContext = CreateContext();
        var (victim, vec) = await SeedAsync(dbContext, "N2SPG");
        var (attacker, _) = await SeedAsync(dbContext, "W7QQQ");
        var service = CreateService(dbContext);
        await service.AddAccreditationAsync(victim.Id, vec.Id, null, CancellationToken.None);
        var accreditationId = (await dbContext.VeVecAccreditations.SingleAsync()).Id;

        var result = await service.RemoveAccreditationAsync(
            accreditationId, null, CancellationToken.None, mustBelongToVolunteerExaminerId: attacker.Id);

        // NotFound rather than a distinct "not yours", so probing ids reveals nothing.
        Assert.Equal(VeManagementResult.NotFound, result);
        Assert.Single(dbContext.VeVecAccreditations);
    }

    [Fact]
    public async Task AVeCanRemoveTheirOwn()
    {
        await using var dbContext = CreateContext();
        var (person, vec) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.AddAccreditationAsync(person.Id, vec.Id, null, CancellationToken.None);
        var accreditationId = (await dbContext.VeVecAccreditations.SingleAsync()).Id;

        var result = await service.RemoveAccreditationAsync(
            accreditationId, null, CancellationToken.None, mustBelongToVolunteerExaminerId: person.Id);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Empty(dbContext.VeVecAccreditations);
    }

    /// <summary>The admin path passes no owner constraint, because it is already authorised for every VE it can reach.</summary>
    [Fact]
    public async Task AnAdminRemovesWithoutAnOwnerConstraint()
    {
        await using var dbContext = CreateContext();
        var (person, vec) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.AddAccreditationAsync(person.Id, vec.Id, userId: 7, CancellationToken.None);
        var accreditationId = (await dbContext.VeVecAccreditations.SingleAsync()).Id;

        var result = await service.RemoveAccreditationAsync(accreditationId, userId: 7, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
    }

    /// <summary>
    /// Who asserted an accreditation is the difference between transcription and self-attestation, so
    /// the audit has to say. Null userId is the app-wide convention for "the VE did this", the same
    /// one VeEmailChangeService uses.
    /// </summary>
    [Fact]
    public async Task SelfServiceChangesAreAuditedAsSuch()
    {
        await using var dbContext = CreateContext();
        var (person, vec) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.AddAccreditationAsync(person.Id, vec.Id, null, CancellationToken.None);

        var entry = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("VeAccreditationAddedBySelf", entry.Action);
        Assert.Null(entry.UserId);
    }

    [Fact]
    public async Task AnAdminsChangeIsAuditedUnderTheirOwnId()
    {
        await using var dbContext = CreateContext();
        var (person, vec) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.AddAccreditationAsync(person.Id, vec.Id, userId: 7, CancellationToken.None);

        var entry = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("VeAccreditationAdded", entry.Action);
        Assert.Equal(7, entry.UserId);
    }

    [Fact]
    public async Task AddingTheSameVecTwiceIsRejected()
    {
        await using var dbContext = CreateContext();
        var (person, vec) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.AddAccreditationAsync(person.Id, vec.Id, null, CancellationToken.None);

        var result = await service.AddAccreditationAsync(person.Id, vec.Id, null, CancellationToken.None);

        Assert.Equal(VeManagementResult.AlreadyAccredited, result);
        Assert.Single(dbContext.VeVecAccreditations);
    }
}
