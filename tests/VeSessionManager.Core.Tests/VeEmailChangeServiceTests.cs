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
/// A VE changing their own email (issue #142 phase 5). The address is the credential for
/// self-service sign-in, so these tests are about takeover and lockout rather than about editing a
/// field.
/// </summary>
public class VeEmailChangeServiceTests
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

    private static (VeEmailChangeService Service, FakeEmailSender Email, FixedTimeProvider Clock) Create(AppDbContext dbContext)
    {
        var clock = new FixedTimeProvider(Now);
        var email = new FakeEmailSender();
        return (new VeEmailChangeService(dbContext, new SystemSettingsService(dbContext, clock), email, clock,
            NullLogger<VeEmailChangeService>.Instance), email, clock);
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(AppDbContext dbContext, string? email, string callSign = "N2SPG")
    {
        var person = new VolunteerExaminer { Name = "Sam Granger", CallSign = callSign, Email = email, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();
        return person;
    }

    private static string CapturedToken(FakeEmailSender email) =>
        email.Sent.Last().HtmlBody.Split("token=")[1].Split('"')[0];

    /// <summary>
    /// The takeover guard, and the reason the whole flow exists: the confirmation goes to the address
    /// they already hold, not the one being requested.
    /// </summary>
    [Fact]
    public async Task ConfirmationGoesToTheOldAddress_AndNothingChangesYet()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);

        var result = await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.ConfirmationSent, result);
        Assert.Equal("old@example.com", email.Sent.Single().ToAddress);
        Assert.Equal("old@example.com", (await dbContext.VolunteerExaminers.SingleAsync()).Email);
    }

    /// <summary>Approval from the old mailbox authorises; naming the address is what catches a typo before it locks them out.</summary>
    [Fact]
    public async Task ConfirmationEmail_NamesTheNewAddress()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);

        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        Assert.Contains("new@example.com", email.Sent.Single().HtmlBody);
    }

    [Fact]
    public async Task FollowingTheLink_AppliesTheChange()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        var (result, newEmail) = await service.ConfirmAsync(CapturedToken(email), CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.Confirmed, result);
        Assert.Equal("new@example.com", newEmail);
        Assert.Equal("new@example.com", (await dbContext.VolunteerExaminers.SingleAsync()).Email);
    }

    [Fact]
    public async Task ConfirmationLink_WorksOnceOnly()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);
        var token = CapturedToken(email);

        Assert.Equal(VeEmailChangeResult.Confirmed, (await service.ConfirmAsync(token, CancellationToken.None)).Result);
        Assert.Equal(VeEmailChangeResult.NotFound, (await service.ConfirmAsync(token, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ExpiredLink_IsRejected()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, clock) = Create(dbContext);
        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);
        var token = CapturedToken(email);

        clock.UtcNow = Now + VeEmailChangeService.TokenLifetime + TimeSpan.FromMinutes(1);

        Assert.Equal(VeEmailChangeResult.NotFound, (await service.ConfirmAsync(token, CancellationToken.None)).Result);
        Assert.Equal("old@example.com", (await dbContext.VolunteerExaminers.SingleAsync()).Email);
    }

    /// <summary>Sign-in resolves an address to one person, so two VEs cannot share one — the second would silently receive the first's links.</summary>
    [Fact]
    public async Task AddressBelongingToAnotherVe_IsRefused()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        await SeedVeAsync(dbContext, "taken@example.com", "NP2UU");
        var (service, _, _) = Create(dbContext);

        var result = await service.RequestAsync(person.Id, "taken@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.AlreadyInUse, result);
    }

    /// <summary>
    /// Re-checked at confirmation, not only at request: the link is valid for a day, and someone else
    /// may have taken the address in between.
    /// </summary>
    [Fact]
    public async Task AddressTakenWhileTheLinkWasOutstanding_IsRefusedAtConfirmation()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        await SeedVeAsync(dbContext, "new@example.com", "NP2UU");

        var (result, _) = await service.ConfirmAsync(CapturedToken(email), CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.AlreadyInUse, result);
        Assert.Equal("old@example.com", (await dbContext.VolunteerExaminers.FirstAsync(v => v.Id == person.Id)).Email);
    }

    /// <summary>Two live links pointing at different addresses, with whichever is clicked last winning, is not a race worth having.</summary>
    [Fact]
    public async Task ASecondRequest_SupersedesTheFirst()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, clock) = Create(dbContext);

        await service.RequestAsync(person.Id, "first@example.com", t => $"https://x/y?token={t}", CancellationToken.None);
        var firstToken = CapturedToken(email);

        clock.UtcNow = Now + VeEmailChangeService.RequestThrottle + TimeSpan.FromMinutes(1);
        await service.RequestAsync(person.Id, "second@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.NotFound, (await service.ConfirmAsync(firstToken, CancellationToken.None)).Result);
        Assert.Equal(VeEmailChangeResult.Confirmed, (await service.ConfirmAsync(CapturedToken(email), CancellationToken.None)).Result);
        Assert.Equal("second@example.com", (await dbContext.VolunteerExaminers.SingleAsync()).Email);
    }

    [Fact]
    public async Task RepeatedRequests_AreThrottled()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, _, _) = Create(dbContext);

        await service.RequestAsync(person.Id, "new@example.com", t => $"?token={t}", CancellationToken.None);
        var second = await service.RequestAsync(person.Id, "other@example.com", t => $"?token={t}", CancellationToken.None);

        Assert.Equal(VeEmailChangeResult.Throttled, second);
    }

    [Fact]
    public async Task RubbishAddress_IsRejected()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, _, _) = Create(dbContext);

        Assert.Equal(VeEmailChangeResult.InvalidEmail,
            await service.RequestAsync(person.Id, "not-an-email", t => $"?token={t}", CancellationToken.None));
    }

    /// <summary>Nothing to confirm against means no confirmed path, and applying it anyway is exactly what this service exists to prevent.</summary>
    [Fact]
    public async Task VeWithNoCurrentAddress_CannotSelfServeAChange()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, null);
        var (service, _, _) = Create(dbContext);

        Assert.Equal(VeEmailChangeResult.NoCurrentEmail,
            await service.RequestAsync(person.Id, "new@example.com", t => $"?token={t}", CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedChange_IsAudited()
    {
        await using var dbContext = CreateContext();
        await ConfigureSystemEmailAsync(dbContext);
        var person = await SeedVeAsync(dbContext, "old@example.com");
        var (service, email, _) = Create(dbContext);
        await service.RequestAsync(person.Id, "new@example.com", t => $"https://x/y?token={t}", CancellationToken.None);

        await service.ConfirmAsync(CapturedToken(email), CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.Action == "VeEmailChangedBySelf");
        Assert.Null(audit.UserId);   // the VE did it, not an admin — inventing one would be untrue
        Assert.Contains("old@example.com", audit.Details);
        Assert.Contains("new@example.com", audit.Details);
    }
}
