using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Linking a login to the VolunteerExaminer record for the same human (#224).
///
/// <para>The suggestion half carries the risk. A call-sign match looks authoritative and is not: the
/// FCC reissues call signs, so the holder today may be a different person from the holder when a VE
/// record was created. Every ambiguous case therefore returns null and asks a human, and these tests
/// are mostly a catalogue of what counts as ambiguous.</para>
/// </summary>
public class UserVolunteerExaminerLinkTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Same hand-wired UserOnlyStore as UserManagementServiceTests — AppDbContext is
    // IdentityUserContext<User,int>, with no Role tables to store.
    private static UserManagementService CreateService(AppDbContext dbContext) =>
        new(new UserManager<User>(
                new UserOnlyStore<User, AppDbContext, int>(dbContext),
                Options.Create(new IdentityOptions()),
                new PasswordHasher<User>(),
                [],
                [new PasswordValidator<User>()],
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                services: null!,
                NullLogger<UserManager<User>>.Instance),
            dbContext,
            new FixedTimeProvider(Now));

    private static async Task<User> SeedUserAsync(AppDbContext dbContext, string? callSign, string email = "u@example.org")
    {
        var user = new User { Name = "A User", Email = email, UserName = email, CallSign = callSign, Role = UserRole.SessionManager };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, string? callSign, string name = "A Person")
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    // ---- Linking ----

    [Fact]
    public async Task LinkingStoresTheRecordAndAudits()
    {
        await using var dbContext = CreateContext();
        var acting = await SeedUserAsync(dbContext, "WX0MIK", "admin@example.org");
        var target = await SeedUserAsync(dbContext, "W9NB", "lead@example.org");
        var person = await SeedVeAsync(dbContext, "W9NB", "Nick Bebout");

        var result = await CreateService(dbContext)
            .SetVolunteerExaminerAsync(target.Id, person.Id, acting.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.Equal(person.Id, dbContext.Users.Single(u => u.Id == target.Id).VolunteerExaminerId);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "UserVolunteerExaminerLinked");
    }

    [Fact]
    public async Task PassingNullClearsTheLink()
    {
        await using var dbContext = CreateContext();
        var acting = await SeedUserAsync(dbContext, "WX0MIK", "admin@example.org");
        var target = await SeedUserAsync(dbContext, "W9NB", "lead@example.org");
        var person = await SeedVeAsync(dbContext, "W9NB");
        var service = CreateService(dbContext);
        await service.SetVolunteerExaminerAsync(target.Id, person.Id, acting.Id, CancellationToken.None);

        var result = await service.SetVolunteerExaminerAsync(target.Id, null, acting.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.Null(dbContext.Users.Single(u => u.Id == target.Id).VolunteerExaminerId);
    }

    /// <summary>
    /// Two logins claiming to be the same examiner is a data error. Reported rather than thrown, so
    /// the page can say who already holds it instead of surfacing a DbUpdateException.
    /// </summary>
    [Fact]
    public async Task AVeAlreadyLinkedToSomeoneElseIsRefused()
    {
        await using var dbContext = CreateContext();
        var acting = await SeedUserAsync(dbContext, "WX0MIK", "admin@example.org");
        var first = await SeedUserAsync(dbContext, "W9NB", "first@example.org");
        var second = await SeedUserAsync(dbContext, "W9NB", "second@example.org");
        var person = await SeedVeAsync(dbContext, "W9NB");
        var service = CreateService(dbContext);
        await service.SetVolunteerExaminerAsync(first.Id, person.Id, acting.Id, CancellationToken.None);

        var result = await service.SetVolunteerExaminerAsync(second.Id, person.Id, acting.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.VolunteerExaminerAlreadyLinked, result);
        Assert.Null(dbContext.Users.Single(u => u.Id == second.Id).VolunteerExaminerId);
    }

    /// <summary>Re-linking the same pair is not a conflict with itself.</summary>
    [Fact]
    public async Task RelinkingTheSamePairSucceeds()
    {
        await using var dbContext = CreateContext();
        var acting = await SeedUserAsync(dbContext, "WX0MIK", "admin@example.org");
        var target = await SeedUserAsync(dbContext, "W9NB", "lead@example.org");
        var person = await SeedVeAsync(dbContext, "W9NB");
        var service = CreateService(dbContext);
        await service.SetVolunteerExaminerAsync(target.Id, person.Id, acting.Id, CancellationToken.None);

        var again = await service.SetVolunteerExaminerAsync(target.Id, person.Id, acting.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, again);
    }

    // ---- Suggestion: only when there is exactly one honest answer ----

    [Fact]
    public async Task SuggestsTheSingleVeSharingTheUsersCallSign()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext, "W9NB");
        var person = await SeedVeAsync(dbContext, "W9NB", "Nick Bebout");
        await SeedVeAsync(dbContext, "KM6Z", "Heather J. Parker");

        var suggestion = await CreateService(dbContext).SuggestVolunteerExaminerAsync(user.Id, CancellationToken.None);

        Assert.Equal(person.Id, suggestion?.Id);
    }

    [Fact]
    public async Task SuggestsNothingWhenTheUserHasNoCallSign()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext, null);
        await SeedVeAsync(dbContext, "W9NB");

        Assert.Null(await CreateService(dbContext).SuggestVolunteerExaminerAsync(user.Id, CancellationToken.None));
    }

    /// <summary>
    /// ExamTools' literal placeholder is shared by every VE it cannot identify — matching on it
    /// once fused two different people, which is why Core/CallSign exists at all.
    /// </summary>
    [Fact]
    public async Task SuggestsNothingForAPlaceholderCallSign()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext, "<UNKNOWN>");
        await SeedVeAsync(dbContext, "<UNKNOWN>");

        Assert.Null(await CreateService(dbContext).SuggestVolunteerExaminerAsync(user.Id, CancellationToken.None));
    }

    /// <summary>
    /// The directory already flags shared call signs as possible duplicates because they may be one
    /// person or two. Picking one here would resolve that question by coin toss.
    /// </summary>
    [Fact]
    public async Task SuggestsNothingWhenTwoVeRecordsShareTheCallSign()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext, "W9NB");
        await SeedVeAsync(dbContext, "W9NB", "Nick Bebout");
        await SeedVeAsync(dbContext, "W9NB", "Someone Else");

        Assert.Null(await CreateService(dbContext).SuggestVolunteerExaminerAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SuggestsNothingWhenTheMatchIsAlreadyLinkedToAnotherUser()
    {
        await using var dbContext = CreateContext();
        var acting = await SeedUserAsync(dbContext, "WX0MIK", "admin@example.org");
        var taken = await SeedUserAsync(dbContext, "W9NB", "taken@example.org");
        var asking = await SeedUserAsync(dbContext, "W9NB", "asking@example.org");
        var person = await SeedVeAsync(dbContext, "W9NB");
        await CreateService(dbContext).SetVolunteerExaminerAsync(taken.Id, person.Id, acting.Id, CancellationToken.None);

        Assert.Null(await CreateService(dbContext).SuggestVolunteerExaminerAsync(asking.Id, CancellationToken.None));
    }

    /// <summary>Case and whitespace must not decide whether two people are the same person.</summary>
    [Fact]
    public async Task SuggestionIgnoresCaseAndSurroundingWhitespace()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext, "  w9nb  ");
        var person = await SeedVeAsync(dbContext, "W9NB");

        var suggestion = await CreateService(dbContext).SuggestVolunteerExaminerAsync(user.Id, CancellationToken.None);

        Assert.Equal(person.Id, suggestion?.Id);
    }
}
