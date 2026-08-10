using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE self-service sign-in link (issue #142 phase 5) — the app's first unauthenticated endpoint
/// that reaches personal data. Every test here is about a way it could leak or be abused rather than
/// about whether the happy path works.
/// </summary>
public class VeSelfServiceLinkServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task ConfigureSystemEmailAsync(AppDbContext dbContext)
    {
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = 1,
            SystemSmtpHost = "smtp.example.com",
            SystemSmtpPort = 587,
            SystemSmtpUsername = "sender@example.com",
            SystemSmtpPassword = "secret",
            SystemSmtpFromAddress = "sender@example.com"
        });
        await dbContext.SaveChangesAsync();
    }

    private static (VeSelfServiceLinkService Service, FakeEmailSender Email, FixedTimeProvider Clock) Create(AppDbContext dbContext)
    {
        var clock = new FixedTimeProvider(Now);
        var email = new FakeEmailSender();
        var service = new VeSelfServiceLinkService(
            dbContext,
            new SystemSettingsService(dbContext, clock),
            email,
            clock,
            NullLogger<VeSelfServiceLinkService>.Instance);
        return (service, email, clock);
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, string email)
    {
        var person = new VolunteerExaminer { Name = "Sam Granger", CallSign = "N2SPG", Email = email, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    private static string CapturedToken(FakeEmailSender email) =>
        email.Sent.Single().HtmlBody.Split("token=")[1].Split('"')[0];

    [Fact]
    public async Task KnownAddress_GetsALink()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, _) = Create(dbContext);

        var result = await service.RequestLinkAsync("sam@example.com", t => $"https://example.com/x?token={t}", CancellationToken.None);

        Assert.Equal(VeSelfServiceRequestResult.Accepted, result);
        Assert.Single(email.Sent);
        Assert.Single(dbContext.VeSelfServiceTokens);
    }

    /// <summary>
    /// The enumeration guard. Anything that distinguishes a known address from an unknown one turns
    /// this into a way to discover which people are VEs on this deployment.
    /// </summary>
    [Fact]
    public async Task UnknownAddress_LooksExactlyLikeSuccess()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var (service, email, _) = Create(dbContext);

        var result = await service.RequestLinkAsync("nobody@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(VeSelfServiceRequestResult.Accepted, result);
        Assert.Empty(email.Sent);                       // nothing sent...
        Assert.Empty(dbContext.VeSelfServiceTokens);    // ...and nothing stored
    }

    /// <summary>Being throttled must look like success too, or the difference itself confirms the address exists.</summary>
    [Fact]
    public async Task ThrottledRequest_AlsoLooksLikeSuccess()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, _) = Create(dbContext);

        await service.RequestLinkAsync("sam@example.com", t => $"?token={t}", CancellationToken.None);
        var second = await service.RequestLinkAsync("sam@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(VeSelfServiceRequestResult.Accepted, second);
        Assert.Single(email.Sent);   // still one
    }

    [Fact]
    public async Task ThrottleLifts_AfterTheWindow()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, clock) = Create(dbContext);

        await service.RequestLinkAsync("sam@example.com", t => $"?token={t}", CancellationToken.None);
        clock.UtcNow = Now + VeSelfServiceLinkService.RequestThrottle + TimeSpan.FromMinutes(1);
        await service.RequestLinkAsync("sam@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(2, email.Sent.Count);
    }

    /// <summary>The raw token must never be at rest. A leaked backup should yield nothing that can be presented.</summary>
    [Fact]
    public async Task OnlyTheHashIsStored()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, _) = Create(dbContext);

        await service.RequestLinkAsync("sam@example.com", t => $"https://example.com/x?token={t}", CancellationToken.None);

        var raw = CapturedToken(email);
        var stored = await dbContext.VeSelfServiceTokens.SingleAsync();
        Assert.NotEqual(raw, stored.TokenHash);
        Assert.DoesNotContain(raw, stored.TokenHash);
    }

    [Fact]
    public async Task ValidToken_ResolvesToTheVe()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestLinkAsync("sam@example.com", t => $"https://example.com/x?token={t}", CancellationToken.None);

        var redeemed = await service.RedeemAsync(CapturedToken(email), CancellationToken.None);

        Assert.NotNull(redeemed);
        Assert.Equal(person.Id, redeemed!.Id);
    }

    /// <summary>An emailed link outlives the email. Consuming it on first use means one found later is inert.</summary>
    [Fact]
    public async Task TokenWorksOnceOnly()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestLinkAsync("sam@example.com", t => $"https://example.com/x?token={t}", CancellationToken.None);
        var raw = CapturedToken(email);

        Assert.NotNull(await service.RedeemAsync(raw, CancellationToken.None));
        Assert.Null(await service.RedeemAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, email, clock) = Create(dbContext);
        await service.RequestLinkAsync("sam@example.com", t => $"https://example.com/x?token={t}", CancellationToken.None);
        var raw = CapturedToken(email);

        clock.UtcNow = Now + VeSelfServiceLinkService.TokenLifetime + TimeSpan.FromMinutes(1);

        Assert.Null(await service.RedeemAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task GarbageToken_IsRejectedWithoutThrowing()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var (service, _, _) = Create(dbContext);

        Assert.Null(await service.RedeemAsync("not-a-real-token", CancellationToken.None));
        Assert.Null(await service.RedeemAsync("", CancellationToken.None));
    }

    /// <summary>A missing sender is the one thing reported honestly — a deployment fault the admin must see, not a fact about any person.</summary>
    [Fact]
    public async Task MissingSystemEmail_IsReported()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings { Id = 1 });
        await dbContext.SaveChangesAsync();
        await SeedVeAsync(dbContext, "sam@example.com");
        var (service, _, _) = Create(dbContext);

        var result = await service.RequestLinkAsync("sam@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(VeSelfServiceRequestResult.SystemEmailNotConfigured, result);
    }

    [Fact]
    public async Task EmailMatchIsCaseInsensitive()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        await SeedVeAsync(dbContext, "Sam@Example.com");
        var (service, email, _) = Create(dbContext);

        await service.RequestLinkAsync("sam@example.COM", t => $"?token={t}", CancellationToken.None);

        Assert.Single(email.Sent);
    }
}
