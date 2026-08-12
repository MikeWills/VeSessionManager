using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #275: no Admin POST handler checks <c>ModelState.IsValid</c>, so a tampered or partial post
/// binds <c>null</c> to a non-nullable <c>string</c> parameter and it is written straight through.
///
/// <para><b>Guarded in the service rather than the page.</b> A handler-level check protects one
/// caller; a service-level one protects every caller, including the next page and any future job
/// that creates the same rows. It is also the only version that can be tested without standing up
/// the whole web host — and an untestable guard is how #233 and #234 ended up unexercised.</para>
///
/// <para>What this prevents concretely: <c>Team.Name</c>, <c>Vec.Name</c>,
/// <c>EmailTemplate.Subject</c> and <c>EmailTemplate.Body</c> are all <c>required</c> columns.
/// Writing null gives a <c>DbUpdateException</c> and an unhandled 500; writing <c>""</c> is worse,
/// because it succeeds and leaves a nameless team or a blank template that every screen then renders
/// as empty.</para>
/// </summary>
public class RequiredInputGuardTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<User> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Admin", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    // ---- Team ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatingATeam_WithNoName_IsRefusedRatherThanThrowingOrCreatingANamelessTeam(string? name)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, team) = await new TeamSettingsService(dbContext, new FixedTimeProvider(Now),
            NullLogger<TeamSettingsService>.Instance).CreateAsync(name!, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.NameRequired, result);
        Assert.Null(team);
        Assert.Empty(await dbContext.Teams.ToListAsync());
    }

    [Fact]
    public async Task CreatingATeam_TrimsTheName_SoLeadingSpaceIsNotANewTeam()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var service = new TeamSettingsService(dbContext, new FixedTimeProvider(Now),
            NullLogger<TeamSettingsService>.Instance);

        await service.CreateAsync("  HRCC  ", user.Id, CancellationToken.None);
        var (second, _) = await service.CreateAsync("HRCC", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.DuplicateName, second);
        Assert.Equal("HRCC", (await dbContext.Teams.SingleAsync()).Name);
    }

    // ---- Vec -------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatingAVec_WithNoName_IsRefused(string? name)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);

        var (result, vec) = await new VecManagementService(dbContext, new FixedTimeProvider(Now))
            .CreateAsync(name!, "arrl", supportsYouthProgram: false, notes: null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.NameRequired, result);
        Assert.Null(vec);
        Assert.Empty(await dbContext.Vecs.ToListAsync());
    }

    [Fact]
    public async Task RenamingAVec_ToBlank_IsRefused_AndLeavesTheNameIntact()
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var service = new VecManagementService(dbContext, new FixedTimeProvider(Now));
        var (_, vec) = await service.CreateAsync("ARRL", "arrl", false, null, user.Id, CancellationToken.None);

        var result = await service.UpdateAsync(vec!.Id, "   ", "arrl", false, null, user.Id, CancellationToken.None);

        Assert.Equal(VecActionResult.NameRequired, result);
        Assert.Equal("ARRL", (await dbContext.Vecs.SingleAsync()).Name);
    }

    // ---- Email templates -------------------------------------------------------------------

    /// <summary>
    /// The one where a blank value succeeds instead of throwing, which is the worse failure: the
    /// template is still "configured", and the next candidate email goes out with an empty subject
    /// or an empty body.
    /// </summary>
    [Theory]
    [InlineData(null, "body")]
    [InlineData("", "body")]
    [InlineData("   ", "body")]
    [InlineData("subject", null)]
    [InlineData("subject", "")]
    [InlineData("subject", "   ")]
    public async Task UpdatingAnEmailTemplate_WithBlankSubjectOrBody_IsRefused(string? subject, string? body)
    {
        await using var dbContext = CreateContext();
        var user = await SeedUserAsync(dbContext);
        var team = new Team { Name = "HRCC", CreatedUtc = Now };
        dbContext.Teams.Add(team);
        var template = new EmailTemplate
        {
            Team = team,
            Key = "RegistrationConfirmation",
            Subject = "Original subject",
            Body = "Original body"
        };
        dbContext.EmailTemplates.Add(template);
        await dbContext.SaveChangesAsync();

        var result = await new EmailTemplateAdminService(dbContext, new FixedTimeProvider(Now))
            .UpdateAsync(template.Id, subject!, body!, user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.ContentRequired, result);

        var reloaded = await dbContext.EmailTemplates.AsNoTracking().SingleAsync();
        Assert.Equal("Original subject", reloaded.Subject);
        Assert.Equal("Original body", reloaded.Body);
    }
}
