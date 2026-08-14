using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Most of these assert a *non-*behaviour: that the forgot-password endpoint reveals nothing about
/// which addresses have accounts. That property is easy to break with a well-meaning "helpful" error
/// message, so it is pinned test-by-test rather than left to code review.
/// </summary>
public class PasswordResetServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(EmailCredentials Credentials, EmailMessage Message)> Sent { get; } = [];
        public Exception? ThrowOnSend { get; set; }

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            Sent.Add((credentials, message));
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserManager<User> CreateUserManager(AppDbContext dbContext)
    {
        var store = new UserOnlyStore<User, AppDbContext, int>(dbContext);
        var manager = new UserManager<User>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<User>(),
            [],
            [new PasswordValidator<User>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<User>>.Instance);

        // Program.cs gets its provider from AddDefaultTokenProviders(). The real
        // DataProtectorTokenProvider lives in the Microsoft.AspNetCore.Identity package, which this
        // test project doesn't reference — and adding a NuGet package needs sign-off per CLAUDE.md.
        // StampTokenProvider below reproduces the one property these tests actually depend on.
        manager.RegisterTokenProvider(TokenOptions.DefaultProvider, new StampTokenProvider());
        return manager;
    }

    /// <summary>
    /// Minimal stand-in for Identity's real token provider. A token is the user's security stamp at
    /// the moment it was minted, so it stops validating as soon as the stamp rotates — which is
    /// exactly what makes a real emailed reset link single-use, since a successful
    /// ResetPasswordAsync rotates the stamp. Deliberately models that and nothing else: expiry and
    /// signing are Identity's job and are not what this suite is asserting.
    /// </summary>
    private sealed class StampTokenProvider : IUserTwoFactorTokenProvider<User>
    {
        public async Task<string> GenerateAsync(string purpose, UserManager<User> manager, User user) =>
            $"{purpose}:{await manager.GetSecurityStampAsync(user)}";

        public async Task<bool> ValidateAsync(string purpose, string token, UserManager<User> manager, User user) =>
            token == $"{purpose}:{await manager.GetSecurityStampAsync(user)}";

        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<User> manager, User user) => Task.FromResult(false);
    }

    private static async Task<SystemSettings> SeedSettingsAsync(AppDbContext dbContext, bool emailConfigured = true)
    {
        var settings = new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            SystemSmtpHost = emailConfigured ? "smtp.example.com" : null,
            SystemSmtpUsername = emailConfigured ? "noreply@example.com" : null,
            SystemSmtpPassword = emailConfigured ? "secret" : null
        };
        dbContext.SystemSettings.Add(settings);
        await dbContext.SaveChangesAsync();
        return settings;
    }

    /// <param name="lockedOut">Deactivated by an admin — LockoutEnd = MaxValue, the sentinel.</param>
    /// <param name="temporarilyLockedOut">
    /// Locked out by failed sign-ins, which is Identity's ordinary behaviour and a completely
    /// different thing (#262). Both set the same column, which is the whole trap.
    /// </param>
    private static async Task<User> SeedUserAsync(
        AppDbContext dbContext, UserManager<User> userManager, bool withPassword = true, bool lockedOut = false,
        bool temporarilyLockedOut = false)
    {
        var user = new User { Name = "Pat Example", Email = "pat@example.com", UserName = "pat@example.com" };
        if (withPassword)
        {
            await userManager.CreateAsync(user, "Valid-Password1!");
        }
        else
        {
            await userManager.CreateAsync(user); // OAuth-only: no password hash
        }

        if (lockedOut)
        {
            // How UserManagementService.DeactivateAsync actually deactivates — there is no IsActive flag.
            await userManager.SetLockoutEnabledAsync(user, true);
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        }

        if (temporarilyLockedOut)
        {
            // What five wrong passwords produce: a few minutes from now, not the sentinel.
            await userManager.SetLockoutEnabledAsync(user, true);
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        return user;
    }

    private static (PasswordResetService Service, FakeEmailSender Email, MutableTimeProvider Clock) CreateService(
        AppDbContext dbContext, UserManager<User> userManager)
    {
        var email = new FakeEmailSender();
        var clock = new MutableTimeProvider(Now);
        var service = new PasswordResetService(
            dbContext, userManager, new SystemSettingsService(dbContext, clock), email, clock,
            NullLogger<PasswordResetService>.Instance);
        return (service, email, clock);
    }

    private static string StubUrl(int userId, string token) => $"https://example.test/reset?userId={userId}";

    // ---- Non-enumeration ----

    [Fact]
    public async Task Request_ForAnUnknownAddress_ReportsAcceptedAndSendsNothing()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("nobody@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Request_ForADeactivatedAccount_ReportsAcceptedAndSendsNothing()
    {
        // A locked-out account must not be recoverable by whoever controls the mailbox.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager, lockedOut: true);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Request_ForAnOAuthOnlyAccount_SendsNothing()
    {
        // Otherwise anyone with mailbox access could ADD a password to an account that deliberately
        // had none, downgrading an SSO-protected login to a password login.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager, withPassword: false);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Request_WhenTheSendThrows_StillReportsAccepted()
    {
        // A send failure that surfaced differently would reintroduce the enumeration oracle.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager);
        var (service, email, _) = CreateService(dbContext, userManager);
        email.ThrowOnSend = new InvalidOperationException("smtp exploded");

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
    }

    // ---- Configuration gate ----

    [Fact]
    public async Task Request_WithNoSystemSenderConfigured_IsReportedToTheAdmin()
    {
        // The one case that is NOT hidden: a deployment misconfiguration, not a fact about an account.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext, emailConfigured: false);
        await SeedUserAsync(dbContext, userManager);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.SystemEmailNotConfigured, result);
        Assert.Empty(email.Sent);
    }

    // ---- Happy path + throttle ----

    [Fact]
    public async Task Request_ForAnEligibleAccount_SendsFromTheSystemSender()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
        var (credentials, message) = Assert.Single(email.Sent);
        Assert.Equal("smtp.example.com", credentials.Host);
        Assert.Equal(0, credentials.TeamId); // system sender, not a team's
        Assert.Equal("pat@example.com", message.ToAddress);
        Assert.Contains("example.test/reset", message.HtmlBody);
        Assert.Equal(Now, dbContext.Users.Single().LastPasswordResetRequestedUtc);
    }

    [Fact]
    public async Task Request_TwiceInsideTheThrottleWindow_SendsOnce()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager);
        var (service, email, clock) = CreateService(dbContext, userManager);

        await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);
        clock.UtcNow = Now.Add(PasswordResetService.RequestThrottle) - TimeSpan.FromSeconds(1);
        var second = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, second); // still indistinguishable
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Request_AfterTheThrottleWindow_SendsAgain()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager);
        var (service, email, clock) = CreateService(dbContext, userManager);

        await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);
        clock.UtcNow = Now.Add(PasswordResetService.RequestThrottle).AddSeconds(1);
        await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(2, email.Sent.Count);
    }

    // ---- Completing a reset ----

    [Fact]
    public async Task Reset_WithAValidToken_ChangesThePasswordAndAudits()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager);
        var (service, _, _) = CreateService(dbContext, userManager);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        var result = await service.ResetAsync(user.Id, token, "Brand-New-Password9!", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(await userManager.CheckPasswordAsync(await userManager.FindByIdAsync(user.Id.ToString()) ?? user, "Brand-New-Password9!"));
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "PasswordReset" && a.EntityId == user.Id);
        Assert.Null(dbContext.Users.Single().LastPasswordResetRequestedUtc); // throttle cleared
    }

    [Fact]
    public async Task Reset_WithAGarbageToken_Fails()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager);
        var (service, _, _) = CreateService(dbContext, userManager);

        var result = await service.ResetAsync(user.Id, "not-a-real-token", "Brand-New-Password9!", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Reset_ReusingATokenAfterASuccessfulReset_Fails()
    {
        // A successful reset rotates the security stamp, which invalidates every outstanding token
        // for that user — that is what makes the emailed link effectively single-use.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager);
        var (service, _, _) = CreateService(dbContext, userManager);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        var first = await service.ResetAsync(user.Id, token, "Brand-New-Password9!", CancellationToken.None);
        var second = await service.ResetAsync(user.Id, token, "Another-Password9!", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Reset_ForADeactivatedAccount_Fails()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager);
        var (service, _, _) = CreateService(dbContext, userManager);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        var result = await service.ResetAsync(user.Id, token, "Brand-New-Password9!", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    // ---- #262: an ordinary failed-login lockout must not disable recovery ----

    /// <summary>
    /// The finding. The guard tested UserManager.IsLockedOutAsync, which is true both for an
    /// admin-deactivated account AND during Identity's ~5-minute failed-login lockout. So burning
    /// five attempts against a known address also killed that user's password reset for the window
    /// — while the response stayed "Accepted" (correctly non-enumerable), so the victim waited for
    /// mail nobody sent. Someone who just locked themselves out by forgetting their password is
    /// exactly who needs this.
    /// </summary>
    [Fact]
    public async Task Request_ForATemporarilyLockedOutAccount_StillSends()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        await SeedUserAsync(dbContext, userManager, temporarilyLockedOut: true);
        var (service, email, _) = CreateService(dbContext, userManager);

        var result = await service.RequestResetAsync("pat@example.com", StubUrl, CancellationToken.None);

        Assert.Equal(PasswordResetRequestResult.Accepted, result);
        Assert.Single(email.Sent);
    }

    /// <summary>
    /// The other half, and why the fix is a narrower test rather than no test: a DEACTIVATED account
    /// must still be unrecoverable by whoever holds the mailbox. Delete the IsDeactivated check
    /// entirely and this fails while the one above passes.
    /// </summary>
    [Fact]
    public async Task Reset_ForATemporarilyLockedOutAccount_IsAllowed_ButNotForADeactivatedOne()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        await SeedSettingsAsync(dbContext);
        var user = await SeedUserAsync(dbContext, userManager, temporarilyLockedOut: true);
        var (service, _, _) = CreateService(dbContext, userManager);

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var ok = await service.ResetAsync(user.Id, token, "New-Password1!", CancellationToken.None);
        Assert.True(ok.Succeeded);

        // Now deactivate the same account and confirm the door is shut.
        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        var token2 = await userManager.GeneratePasswordResetTokenAsync(user);
        var refused = await service.ResetAsync(user.Id, token2, "Another-Password1!", CancellationToken.None);
        Assert.False(refused.Succeeded);
    }
}
