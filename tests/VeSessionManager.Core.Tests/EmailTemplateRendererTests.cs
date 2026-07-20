using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class EmailTemplateRendererTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static EmailTemplateRenderer CreateRenderer(AppDbContext dbContext) =>
        new(dbContext, NullLogger<EmailTemplateRenderer>.Instance);

    [Fact]
    public async Task KnownPlaceholders_AreSubstitutedInSubjectAndBody()
    {
        await using var dbContext = CreateContext();
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "Test",
            Subject = "Hello {{FirstName}}",
            Body = "<p>Hi {{FirstName}}, your session is {{SessionDate}}.</p>"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync("Test",
            new Dictionary<string, string> { ["FirstName"] = "Roana", ["SessionDate"] = "July 24" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Hello Roana", result.Subject);
        Assert.Equal("<p>Hi Roana, your session is July 24.</p>", result.Body);
    }

    [Fact]
    public async Task EmptyStringValue_ForAKnownPlaceholder_SubstitutesToBlank_NoWarning()
    {
        await using var dbContext = CreateContext();
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "Test",
            Subject = "Subject",
            Body = "Payment link: {{PaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync("Test",
            new Dictionary<string, string> { ["PaymentLinkUrl"] = "" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Payment link: ", result.Body);
    }

    [Fact]
    public async Task UnknownPlaceholder_IsLeftLiteral_NotSilentlyDropped()
    {
        await using var dbContext = CreateContext();
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "Test",
            Subject = "Subject",
            Body = "Hi {{Typo}}, welcome."
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync("Test",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.NotNull(result);
        // Left as the literal token, not blanked out and not silently sent as if nothing were wrong.
        Assert.Equal("Hi {{Typo}}, welcome.", result.Body);
    }

    [Fact]
    public async Task MissingTemplateKey_ReturnsNull_DoesNotThrow()
    {
        await using var dbContext = CreateContext();

        var result = await CreateRenderer(dbContext).RenderAsync("DoesNotExist", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task MultiplePlaceholders_SameKey_AllSubstituted()
    {
        await using var dbContext = CreateContext();
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "Test",
            Subject = "Subject",
            Body = "{{Name}}, {{Name}} again, and {{Other}}."
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateRenderer(dbContext).RenderAsync("Test",
            new Dictionary<string, string> { ["Name"] = "Roana", ["Other"] = "x" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Roana, Roana again, and x.", result.Body);
    }
}
