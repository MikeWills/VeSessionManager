using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A signed-in user setting the first email address on their own VE record (#226).
///
/// <para>The reason this method exists at all is a loop that cannot be opened from outside:
/// VeEmailChangeService confirms a change by mailing the address already on file, so a VE with no
/// address can never acquire one through the flow meant for them. One VE of 176 has an address, so
/// that is the normal state and not an edge case.</para>
///
/// <para>What these tests actually pin is the <b>narrowness</b> of the exception. Writing an address
/// without confirmation is safe only when there is none to divert; the moment one exists, the
/// confirmed path must be the only one. A second, weaker route to the same field — reachable by
/// whoever is already signed in — is how the careful route stops being the one that gets used.</para>
/// </summary>
public class VeOwnEmailTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerManagementService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<VolunteerExaminer> SeedAsync(AppDbContext dbContext, string callSign, string? email = null)
    {
        var person = new VolunteerExaminer { Name = "A Person", CallSign = callSign, Email = email };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    private const int ActingUserId = 7;

    [Fact]
    public async Task SetsTheAddressWhenThereIsNoneOnFile()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB");

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, "nick@example.org", ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Equal("nick@example.org", dbContext.VolunteerExaminers.Single(v => v.Id == person.Id).Email);
    }

    /// <summary>
    /// The whole point of the guard. An existing address is the thing the confirmation flow protects,
    /// and a direct write here would be a way around it for anyone already signed in.
    /// </summary>
    [Fact]
    public async Task RefusesWhenAnAddressIsAlreadyOnFile()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB", "old@example.org");

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, "attacker@example.org", ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.EmailAlreadySet, result);
        Assert.Equal("old@example.org", dbContext.VolunteerExaminers.Single(v => v.Id == person.Id).Email);
    }

    /// <summary>Whitespace is not an address, and an all-whitespace value must not read as "already set" either.</summary>
    [Fact]
    public async Task TreatsAWhitespaceOnlyStoredAddressAsUnset()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB", "   ");

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, "nick@example.org", ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public async Task RejectsSomethingThatIsNotAnAddress(string candidate)
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB");

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, candidate, ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.InvalidEmail, result);
        Assert.Null(dbContext.VolunteerExaminers.Single(v => v.Id == person.Id).Email);
    }

    /// <summary>
    /// Sign-in resolves an address to one person, so two VEs sharing one means somebody silently
    /// receives another's links. Same rule as every other write to this field.
    /// </summary>
    [Fact]
    public async Task RefusesAnAddressAnotherVeAlreadyUses()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext, "KM6Z", "taken@example.org");
        var person = await SeedAsync(dbContext, "W9NB");

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, "TAKEN@example.org", ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.EmailAlreadyInUse, result);
        Assert.Null(dbContext.VolunteerExaminers.Single(v => v.Id == person.Id).Email);
    }

    [Fact]
    public async Task ReportsNotFoundForAnUnknownRecord()
    {
        await using var dbContext = CreateContext();

        var result = await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(4242, "nick@example.org", ActingUserId, CancellationToken.None);

        Assert.Equal(VeManagementResult.NotFound, result);
    }

    /// <summary>
    /// The address is recorded in the entry on purpose: it is the first one on file, and what it was
    /// set to is the whole audit value if links later go somewhere unexpected.
    /// </summary>
    [Fact]
    public async Task AuditsTheActingUserAndTheAddress()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB");

        await CreateService(dbContext)
            .SetOwnEmailWhenUnsetAsync(person.Id, "nick@example.org", ActingUserId, CancellationToken.None);

        var entry = dbContext.AuditLogs.Single(a => a.Action == "VeEmailSetBySelf");
        Assert.Equal(ActingUserId, entry.UserId);
        Assert.Contains("nick@example.org", entry.Details);
    }

    // ---- The contact-details audit, which the in-app page changed ----

    /// <summary>
    /// Self-service passes no acting user because none exists there — an emailed link involves no
    /// account. Naming one would make the trail say something untrue, so null is the honest value and
    /// stays the default.
    /// </summary>
    [Fact]
    public async Task ContactDetailsFromSelfServiceStillAuditWithNoActingUser()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB");

        await CreateService(dbContext).UpdateOwnContactDetailsAsync(
            person.Id, Details("Nick Bebout"), CancellationToken.None);

        var entry = dbContext.AuditLogs.Single(a => a.Action == "VeContactDetailsUpdatedBySelf");
        Assert.Null(entry.UserId);
        Assert.Contains("self-service link", entry.Details);
    }

    /// <summary>And from inside the app an account really did act, so the trail names it.</summary>
    [Fact]
    public async Task ContactDetailsFromInsideTheAppNameTheSignedInUser()
    {
        await using var dbContext = CreateContext();
        var person = await SeedAsync(dbContext, "W9NB");

        await CreateService(dbContext).UpdateOwnContactDetailsAsync(
            person.Id, Details("Nick Bebout"), CancellationToken.None, ActingUserId);

        var entry = dbContext.AuditLogs.Single(a => a.Action == "VeContactDetailsUpdatedBySelf");
        Assert.Equal(ActingUserId, entry.UserId);
        Assert.Contains("while signed in", entry.Details);
    }

    private static VeSelfContactDetails Details(string name) =>
        new(name, null, null, null, null, null, null, null, VeContactPreference.Email);
}
