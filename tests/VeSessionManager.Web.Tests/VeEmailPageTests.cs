using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Emailing a team's VEs from the directory, and the unsubscribe behind it (#191).
///
/// <para>The unsubscribe tests are the ones with a legal edge: CAN-SPAM wants an opt-out that works
/// without an account, keeps working long after the message, and is honoured promptly. Each of those
/// is a property of the page rather than of the service, so each is asserted here.</para>
/// </summary>
public class VeEmailPageTests
{
    private static async Task ConfigureTeamEmailAsync(WebAppFactory factory, bool subscriptionsEnabled = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var team = await db.Teams.FirstAsync(t => t.Id == factory.Seeded.TeamId);
        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";
        team.VeEmailSubscriptionsEnabled = subscriptionsEnabled;

        db.EmailSettings.Add(new EmailSettings
        {
            TeamId = factory.Seeded.TeamId,
            FromAddress = "noreply@example.org",
            FromDisplayName = "Test Team",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });

        // The harness's seeded VE has no address; give it one so it can actually be mailed.
        var ve = await db.VolunteerExaminers.FirstAsync(v => v.Id == factory.Seeded.VolunteerExaminerId);
        ve.Email = "testve@example.com";
        await db.SaveChangesAsync();
    }

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

    [Fact]
    public async Task TheDirectoryLinksToTheMessageScreen()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/VeDirectory");

        Assert.Contains("/SessionManager/VeEmail", html);
    }

    [Fact]
    public async Task TheDirectoryShowsWhetherContactDetailsExist_NeverTheDetails()
    {
        // Mike, 2026-08-16: presence, not values. The addresses stay on the VE's own page and in the
        // CSV export.
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/VeDirectory");

        Assert.Contains("bi-envelope-fill", html);
        Assert.DoesNotContain("testve@example.com", html);
    }

    [Fact]
    public async Task SendingReachesTheTeamsVes_AndCarriesAnUnsubscribeLink()
    {
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        await PostWithTokenAsync(
            client, $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("template", ""),
            ("subscribedOnly", "false"),
            ("Subject", "Field Day"),
            ("Body", "<p>Hi {{VeName}}</p>"),
            ("SelectedVeIds", factory.Seeded.VolunteerExaminerId.ToString()));

        var sent = Assert.Single(factory.SentEmails);
        Assert.Equal("testve@example.com", sent.ToAddress);
        Assert.Contains("/ve/unsubscribe/", sent.HtmlBody);
    }

    [Fact]
    public async Task TheUnsubscribeLinkWorksWithoutSigningIn_AndOnlyOnAPost()
    {
        // Anonymous because a VE has no account, and POST-only because mail clients and scanners
        // prefetch links — a GET that unsubscribed on sight would opt people out silently.
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var adminClient = factory.CreateClientAs(UserRole.SystemAdmin);

        await PostWithTokenAsync(
            adminClient, $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("template", ""),
            ("subscribedOnly", "false"),
            ("Subject", "Field Day"),
            ("Body", "<p>Hi {{VeName}}</p>"),
            ("SelectedVeIds", factory.Seeded.VolunteerExaminerId.ToString()));

        var link = factory.SentEmails[0].HtmlBody.Split("/ve/unsubscribe/")[1].Split('"')[0].Split('<')[0].Trim();
        // No role header: nobody is signed in.
        var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await anonymous.GetAsync($"/ve/unsubscribe/{link}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ve = await db.VolunteerExaminers.FirstAsync(v => v.Id == factory.Seeded.VolunteerExaminerId);
            // Merely looking must not change anything.
            Assert.Null(ve.EmailUnsubscribedUtc);
        }

        await PostWithTokenAsync(
            anonymous, $"/ve/unsubscribe/{link}", $"/ve/unsubscribe/{link}", ("resubscribe", "false"));

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unsubscribed = await verifyDb.VolunteerExaminers.FirstAsync(v => v.Id == factory.Seeded.VolunteerExaminerId);
        Assert.NotNull(unsubscribed.EmailUnsubscribedUtc);
    }

    [Fact]
    public async Task AnUnknownUnsubscribeToken_SaysNothingAboutWhetherItExists()
    {
        using var factory = new WebAppFactory();
        var anonymous = factory.CreateClient();

        var html = await anonymous.GetStringAsync("/ve/unsubscribe/0000000000000000000000000000000000000000000000000000000000000000");

        Assert.Contains("not valid", html);
    }

    [Fact]
    public async Task TheSubscribeBoxOnlyAppearsWhenTheTeamAllowsIt()
    {
        // Mike's reason for the switch: a team that does not email every VE about every session must
        // not show a box implying it does.
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory, subscriptionsEnabled: false);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var off = await client.GetStringAsync($"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}");
        Assert.DoesNotContain("Subscribed only", off);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync(t => t.Id == factory.Seeded.TeamId);
            team.VeEmailSubscriptionsEnabled = true;
            await db.SaveChangesAsync();
        }

        var on = await client.GetStringAsync($"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}");
        Assert.Contains("Subscribed only", on);
    }

    [Fact]
    public async Task AVeOnAnotherTeam_CannotBeEmailed()
    {
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);

        int otherVeId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var otherTeam = new Team { Name = "OTHER-TEAM", ExamToolsTeamCode = "OTHER" };
            db.Teams.Add(otherTeam);
            await db.SaveChangesAsync();

            var ve = new VolunteerExaminer { Name = "Other Team VE", Email = "otherve@example.com" };
            db.VolunteerExaminers.Add(ve);
            await db.SaveChangesAsync();
            db.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = ve.Id, TeamId = otherTeam.Id, IsActive = true });
            await db.SaveChangesAsync();
            otherVeId = ve.Id;
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var response = await PostWithTokenAsync(
            client, $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            $"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}",
            ("teamId", factory.Seeded.TeamId.ToString()),
            ("template", ""),
            ("subscribedOnly", "false"),
            ("Subject", "Field Day"),
            ("Body", "<p>Hi {{VeName}}</p>"),
            ("SelectedVeIds", otherVeId.ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.SentEmails);
    }

    [Fact]
    public async Task TheRecipientListOffersATagFilter_BuiltFromTheTagsActuallyInUse()
    {
        // #394. Built from the people listed rather than the team's whole vocabulary, so it can never
        // offer a tag that would match nobody.
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tag = new VeTag { TeamId = factory.Seeded.TeamId, Name = "Liaison" };
            var unused = new VeTag { TeamId = factory.Seeded.TeamId, Name = "Never assigned" };
            db.VeTags.AddRange(tag, unused);
            await db.SaveChangesAsync();

            var membership = await db.VeTeamMemberships
                .FirstAsync(m => m.TeamId == factory.Seeded.TeamId && m.VolunteerExaminerId == factory.Seeded.VolunteerExaminerId);
            db.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag.Id });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}");

        Assert.Contains("data-ve-tag-filter", html);
        Assert.Contains("Liaison", html);
        Assert.DoesNotContain("Never assigned", html);
        // The guest sentinel, whose leading space is load-bearing — see VeTagFilter.
        Assert.Contains("Untagged (guests)", html);
    }

    [Fact]
    public async Task WithNoTagsInUse_NoTagFilterIsOffered()
    {
        using var factory = new WebAppFactory();
        await ConfigureTeamEmailAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/SessionManager/VeEmail?teamId={factory.Seeded.TeamId}");

        Assert.DoesNotContain("data-ve-tag-filter", html);
    }
}
