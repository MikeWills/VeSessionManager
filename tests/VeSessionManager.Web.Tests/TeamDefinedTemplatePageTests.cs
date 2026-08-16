using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Templates a team writes for itself, through the admin page and into the compose picker (#144).
///
/// <para>The two ends have to agree on one thing: a team-defined template is known by the name
/// somebody typed, not by its generated key. A picker showing <c>Custom.field-day</c>, or a history
/// row recorded under it, is the failure this covers.</para>
/// </summary>
public class TeamDefinedTemplatePageTests
{
    private const string AdminUrl = "/Admin/EmailTemplates";

    private static async Task<HttpResponseMessage> PostWithTokenAsync(
        HttpClient client, string url, string tokenPage, params (string Name, string Value)[] fields)
    {
        var page = await client.GetStringAsync(tokenPage);
        var token = Regex.Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).ToList();
        form.Add(new KeyValuePair<string, string>("__RequestVerificationToken", token));
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    private static async Task<EmailTemplate?> SingleUserTemplateAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.EmailTemplates.FirstOrDefaultAsync(t => t.IsUserDefined);
    }

    [Fact]
    public async Task AnAdminCanCreateOne_AndItIsListedByTheNameTheyTyped()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await PostWithTokenAsync(
            client, $"{AdminUrl}?handler=Create&teamId={factory.Seeded.TeamId}", $"{AdminUrl}?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("name", "Field Day invite"),
            ("subject", "Come to Field Day"),
            ("body", "<p>Hi {{CandidateFirstName}}</p>"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var created = await SingleUserTemplateAsync(factory);
        Assert.NotNull(created);
        Assert.Equal("Field Day invite", created!.DisplayName);

        var html = await client.GetStringAsync($"{AdminUrl}?teamId={factory.Seeded.TeamId}");
        Assert.Contains("Field Day invite", html);
        // Never the generated key: it is an implementation detail of keeping the two populations apart.
        Assert.DoesNotContain(created.Key, html);
    }

    [Fact]
    public async Task ACreatedTemplate_AppearsInTheComposePicker_UnderItsName()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        await PostWithTokenAsync(
            client, $"{AdminUrl}?handler=Create&teamId={factory.Seeded.TeamId}", $"{AdminUrl}?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("name", "Field Day invite"),
            ("subject", "Come to Field Day"),
            ("body", "<p>Hi {{CandidateFirstName}}</p>"));

        var created = await SingleUserTemplateAsync(factory);
        var html = await client.GetStringAsync($"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}");

        Assert.Contains("Field Day invite", html);
        Assert.Contains($"value=\"{created!.Key}\"", html);
    }

    [Fact]
    public async Task ChoosingATeamDefinedTemplate_FillsTheDraftFromIt()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        await PostWithTokenAsync(
            client, $"{AdminUrl}?handler=Create&teamId={factory.Seeded.TeamId}", $"{AdminUrl}?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("name", "Field Day invite"),
            ("subject", "Come to Field Day"),
            ("body", "<p>Talk-in on 146.52</p>"));

        var created = await SingleUserTemplateAsync(factory);
        var html = await client.GetStringAsync(
            $"/SessionManager/CandidateEmail/{factory.Seeded.SessionId}?template={created!.Key}");

        Assert.Contains("Come to Field Day", html);
        Assert.Contains("Talk-in on 146.52", html);
    }

    [Fact]
    public async Task AShippedTemplate_OffersNoDeleteButton()
    {
        using var factory = new WebAppFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EmailTemplates.Add(new EmailTemplate
            {
                TeamId = factory.Seeded.TeamId,
                Key = "FelonyDisclosureInstructions",
                Subject = "Subject",
                Body = "<p>Body</p>"
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"{AdminUrl}?teamId={factory.Seeded.TeamId}");

        Assert.Contains("Felony disclosure instructions", html);
        Assert.DoesNotContain("Delete template", html);
    }

    [Fact]
    public async Task DeletingAShippedTemplate_IsRefusedEvenWhenPostedDirectly()
    {
        // The buttons are hidden for these, so this is the guard behind the courtesy: a hand-made
        // POST must not remove a template a background job sends by name.
        using var factory = new WebAppFactory();
        int templateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = new EmailTemplate
            {
                TeamId = factory.Seeded.TeamId,
                Key = "RegistrationConfirmation",
                Subject = "Subject",
                Body = "<p>Body</p>"
            };
            db.EmailTemplates.Add(template);
            await db.SaveChangesAsync();
            templateId = template.Id;
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        await PostWithTokenAsync(
            client, $"{AdminUrl}?handler=Delete", $"{AdminUrl}?teamId={factory.Seeded.TeamId}",
            ("templateId", templateId.ToString()));

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await verifyDb.EmailTemplates.FirstOrDefaultAsync(t => t.Id == templateId));
    }

    [Fact]
    public async Task ATemplateOnAnotherTeam_CannotBeDeleted()
    {
        // The IDOR re-check: authorize against the template's own team, never a posted one.
        using var factory = new WebAppFactory();
        int otherTemplateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var otherTeam = new Team { Name = "OTHER-TEAM", ExamToolsTeamCode = "OTHER" };
            db.Teams.Add(otherTeam);
            await db.SaveChangesAsync();

            var template = new EmailTemplate
            {
                TeamId = otherTeam.Id,
                Key = "Custom.theirs",
                DisplayName = "Theirs",
                IsUserDefined = true,
                Subject = "Subject",
                Body = "<p>Body</p>"
            };
            db.EmailTemplates.Add(template);

            var user = await db.Users.FirstAsync(u => u.Id == factory.Seeded.UserId);
            user.Role = UserRole.TeamAdmin;
            await db.SaveChangesAsync();
            otherTemplateId = template.Id;
        }

        var client = factory.CreateClientAs(UserRole.TeamAdmin);
        var response = await PostWithTokenAsync(
            client, $"{AdminUrl}?handler=Delete", $"{AdminUrl}?teamId={factory.Seeded.TeamId}",
            ("templateId", otherTemplateId.ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await verifyDb.EmailTemplates.FirstOrDefaultAsync(t => t.Id == otherTemplateId));
    }
}
