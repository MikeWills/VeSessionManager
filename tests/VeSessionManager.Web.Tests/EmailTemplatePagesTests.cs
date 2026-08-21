using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The list / preview / edit split (#395).
///
/// <para>Email Templates used to render every template's full editor — trigger panel, placeholder
/// chips, subject, Quill — stacked on one page. "Kludgy" was Mike's word for it. These pin the shape
/// that replaced it: a list that says what exists, a preview that shows what goes out, and an editor
/// that handles one template.</para>
/// </summary>
public class EmailTemplatePagesTests
{
    private static async Task<int> SeedTemplateAsync(
        WebAppFactory factory, string key = "RegistrationConfirmation", string? body = null, bool userDefined = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var template = new EmailTemplate
        {
            TeamId = factory.Seeded.TeamId,
            Key = key,
            DisplayName = userDefined ? "Field Day invite" : null,
            IsUserDefined = userDefined,
            Subject = "Hello {{CandidateFirstName}}",
            Body = body ?? "<p>Hi {{CandidateFirstName}}, see you on {{SessionDate}}.</p>"
        };
        db.EmailTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    [Fact]
    public async Task TheListShowsEachTemplateOnce_WithViewAndEdit_AndNoInlineEditor()
    {
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplates?teamId={factory.Seeded.TeamId}");

        Assert.Contains($"/Admin/EmailTemplatePreview/{id}", html);
        Assert.Contains($"/Admin/EmailTemplateEdit/{id}", html);
        // The whole point of #395: the editor is not on this page any more.
        Assert.DoesNotContain("editor-surface", html);
        Assert.DoesNotContain("quill", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creating a template is its own page now, reached from a button that is always in the same
    /// place. It was a form at the bottom of this list, which with eleven shipped templates above it
    /// is a long way down — "past the fold" is not a discoverability strategy.
    /// </summary>
    [Fact]
    public async Task TheListLinksToACreatePage_RatherThanCarryingTheFormItself()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplates?teamId={factory.Seeded.TeamId}");

        Assert.Contains($"/Admin/EmailTemplateNew?teamId={factory.Seeded.TeamId}", html);
        Assert.DoesNotContain("new-template-body", html);
        Assert.DoesNotContain("handler=\"Create\"", html);
    }

    /// <summary>
    /// Row actions are one labelled menu, matching every other admin table.
    ///
    /// <para>⚠️ <b>This used to assert two bare icons</b> — an eye and a pencil — with the third
    /// action, Rules, living somewhere else entirely as a link inside the Sent column. Three
    /// destinations for one row, in two places, none of them labelled. Mike asked for the menu
    /// (2026-08-21): <i>"So the dropdown will have edit, view, and rules."</i> A word says what an icon
    /// can only approximate, and putting all three in one place is the point of the change.</para>
    ///
    /// <para>The kebab button keeps an aria-label, for the same reason the icons carried one: an
    /// icon-only control is only as good as what a screen reader makes of it.</para>
    /// </summary>
    [Fact]
    public async Task RowActionsAreOneLabelledMenu()
    {
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplates?teamId={factory.Seeded.TeamId}");

        Assert.Contains("class=\"kebab\"", html);
        Assert.Contains("aria-label=\"Actions for", html);
        Assert.Contains(">View</a>", html);
        Assert.Contains(">Edit</a>", html);

        // The destinations are unchanged — the menu is presentation, not different actions.
        Assert.Contains($"/Admin/EmailTemplateEdit/{id}", html);
        Assert.Contains($"/Admin/EmailTemplatePreview/{id}", html);
    }

    /// <summary>
    /// The third entry, and the one that was hardest to find before, is <b>in the menu</b> rather than
    /// in a column about scheduling — and it is one entry whichever state the template is in.
    /// </summary>
    [Fact]
    public async Task TheRulesEntry_LivesInTheMenu_NotBesideTheSchedule()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, key: "SomethingNothingSends");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplates?teamId={factory.Seeded.TeamId}");

        Assert.Matches("MessageRuleNew[^\"]*templateId=", html);

        // The old inline affordance is gone — two ways in was the confusion this replaced.
        Assert.DoesNotContain("Add another rule", html);
    }

    /// <summary>
    /// "Which rule sends this, and let me change it" is one question. A template a rule sends links to
    /// that rule; one nothing sends offers the way to make it automatic, which otherwise requires
    /// already knowing the Message Rules page exists.
    /// </summary>
    [Fact]
    public async Task ATemplateNoRuleSends_OffersAWayToAddOne()
    {
        using var factory = new WebAppFactory();
        await SeedTemplateAsync(factory, key: "SomethingNothingSends");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplates?teamId={factory.Seeded.TeamId}");

        // #409: it carries the template now, rather than dropping you on Message Rules to find it
        // again among thirty others.
        Assert.Contains(">Add a rule</a>", html);   // moved into the row menu 2026-08-21 — same destination, one place
        Assert.Matches("MessageRuleNew[^\"]*templateId=", html);
    }

    [Fact]
    public async Task ThePreviewRendersTheBody_WithSampleValuesRatherThanTokens()
    {
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplatePreview/{id}");

        Assert.Contains("Ana", html);
        Assert.DoesNotContain("{{CandidateFirstName}}", html);
    }

    [Fact]
    public async Task ThePreviewNamesPlaceholdersNothingWillFillIn()
    {
        // The renderer leaves an unknown token as literal text, so it is already visible — naming it
        // is what turns "why does it say that" into "that is a typo".
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory, body: "<p>Hi {{CandidateFirstNmae}}</p>");
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplatePreview/{id}");

        Assert.Contains("nothing will fill in", html);
        Assert.Contains("CandidateFirstNmae", html);
    }

    [Fact]
    public async Task ThePreviewRendersTheBodyInASandboxedFrame()
    {
        // The body is stored HTML somebody wrote. Dropped into this page it could restyle or overlay
        // the admin UI around it; `sandbox` with no allow-* blocks scripts outright as well.
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/EmailTemplatePreview/{id}");

        Assert.Contains("<iframe sandbox", html);
    }

    [Fact]
    public async Task TheEditorSavesOneTemplate_AndReturnsToTheList()
    {
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetStringAsync($"/Admin/EmailTemplateEdit/{id}");
        var token = System.Text.RegularExpressions.Regex
            .Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"").Groups[1].Value;

        var response = await client.PostAsync($"/Admin/EmailTemplateEdit/{id}", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Subject", "Rewritten subject"),
            new KeyValuePair<string, string>("Body", "<p>Rewritten body</p>"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.EmailTemplates.FirstAsync(t => t.Id == id);
        Assert.Equal("Rewritten subject", saved.Subject);
    }

    [Fact]
    public async Task ATemplateOnAnotherTeam_CannotBeOpenedOrPreviewed()
    {
        using var factory = new WebAppFactory();
        int otherId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var otherTeam = new Team { Name = "OTHER-TEAM", ExamToolsTeamCode = "OTHER" };
            db.Teams.Add(otherTeam);
            await db.SaveChangesAsync();

            var template = new EmailTemplate
            {
                TeamId = otherTeam.Id,
                Key = "RegistrationConfirmation",
                Subject = "Theirs",
                Body = "<p>Theirs</p>"
            };
            db.EmailTemplates.Add(template);

            var user = await db.Users.FirstAsync(u => u.Id == factory.Seeded.UserId);
            user.Role = UserRole.TeamAdmin;
            await db.SaveChangesAsync();
            otherId = template.Id;
        }

        var client = factory.CreateClientAs(UserRole.TeamAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Admin/EmailTemplateEdit/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Admin/EmailTemplatePreview/{otherId}")).StatusCode);
    }

    [Fact]
    public async Task AUserDefinedTemplate_CanBeRenamedFromItsEditor()
    {
        using var factory = new WebAppFactory();
        var id = await SeedTemplateAsync(factory, key: "Custom.field-day", userDefined: true);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetStringAsync($"/Admin/EmailTemplateEdit/{id}");
        var token = System.Text.RegularExpressions.Regex
            .Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"").Groups[1].Value;

        await client.PostAsync($"/Admin/EmailTemplateEdit/{id}?handler=Rename", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("name", "Field Day 2027"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var renamed = await db.EmailTemplates.FirstAsync(t => t.Id == id);
        Assert.Equal("Field Day 2027", renamed.DisplayName);
        // The key never moves: history rows and any open compose screen refer to it.
        Assert.Equal("Custom.field-day", renamed.Key);
    }
}
