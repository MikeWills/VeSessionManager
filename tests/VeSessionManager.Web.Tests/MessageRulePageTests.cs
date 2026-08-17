using System.Text.RegularExpressions;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The Message Rules screen (#401, PR2).
///
/// <para>Two things here cannot be caught by a service test, which is why they are asserted against
/// real markup: that <b>every trigger point renders even with no rules on it</b> — a section that
/// appears only once configured is one nobody discovers — and that the form's field names match what
/// the handler binds. #144 shipped a hidden field whose name did not match its bind name, and no send
/// test could catch it, because a hand-built POST body never reads the markup.</para>
/// </summary>
public class MessageRulePageTests
{
    private static async Task<int> SeedRuleAsync(
        WebAppFactory factory, MessageTrigger trigger = MessageTrigger.BeforeSessionStart,
        int? hours = 24, bool enabled = true, string templateKey = "DayBeforeReminder")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = factory.Seeded.TeamId, Key = templateKey, Subject = "s", Body = "b"
        });

        var rule = new MessageRule
        {
            TeamId = factory.Seeded.TeamId,
            Name = "Day before",
            Trigger = trigger,
            ParameterHours = hours,
            TemplateKey = templateKey,
            IsEnabled = enabled,
            CreatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.MessageRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    [Fact]
    public async Task EveryTriggerPointRenders_EvenWithNoRulesOnIt()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        foreach (var definition in MessageTriggerDefinitions.All)
        {
            Assert.Contains(MessageTriggerLabels.Label(definition.Trigger), html);
        }

        // And says so, rather than leaving a silent gap. "Nothing happens here" is the most useful
        // thing this page has to tell somebody who has never configured it.
        Assert.Contains("No rules — nothing is sent at this point.", html);
    }

    [Fact]
    public async Task ARuleRendersItsScheduleInWordsAndLinksToItsEditor()
    {
        using var factory = new WebAppFactory();
        var id = await SeedRuleAsync(factory, hours: 120);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        // 120 hours read back as "5 days" — the team set hours, and nobody should have to divide.
        Assert.Contains("5 days", html);
        Assert.Contains($"/Admin/MessageRuleEdit/{id}", html);
    }

    /// <summary>
    /// Both actions, because they answer different questions: switching off is "not right now" and
    /// keeps the rule on screen, deleting is "we do not do this". Delete goes through a confirmation
    /// — it cannot be undone, and unlike switching off it does not come back on the next Worker start.
    /// </summary>
    [Fact]
    public async Task ARuleOffersBothSwitchOffAndDelete_AndDeleteIsConfirmed()
    {
        using var factory = new WebAppFactory();
        var id = await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        Assert.Contains("Switch off", html);
        Assert.Contains($"deleteRule-{id}", html);
        Assert.Contains("Delete rule", html);
        // The modal says which of the two the reader probably wants, since the destructive one is the
        // easier click to reach for.
        Assert.Contains("Switch off</strong> instead", html);
    }

    [Fact]
    public async Task ADisabledRuleSaysSo_AndOffersToSwitchItBackOn()
    {
        using var factory = new WebAppFactory();
        await SeedRuleAsync(factory, enabled: false);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        Assert.Contains("Switch on", html);
    }

    /// <summary>
    /// A rule pointing at a template that is gone fails every night with one log line. The list says
    /// so instead — the only place anybody would notice.
    /// </summary>
    [Fact]
    public async Task ARulePointingAtAMissingTemplate_IsFlaggedOnTheRow()
    {
        using var factory = new WebAppFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MessageRules.Add(new MessageRule
            {
                TeamId = factory.Seeded.TeamId,
                Name = "Points at nothing",
                Trigger = MessageTrigger.BeforeSessionStart,
                ParameterHours = 24,
                TemplateKey = "NoSuchTemplate",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        Assert.Contains("Template missing", html);
    }

    /// <summary>
    /// The create form's field names are what the handler binds — <c>teamId</c>, <c>trigger</c>,
    /// <c>name</c>, <c>parameterHours</c>, <c>recipient</c>, <c>templateKey</c>. A mismatch here binds
    /// a default silently, and only reading the markup catches it (#144).
    /// </summary>
    [Fact]
    public async Task TheCreateFormPostsTheNamesTheHandlerBinds()
    {
        using var factory = new WebAppFactory();
        await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        foreach (var field in new[] { "teamId", "trigger", "name", "parameterHours", "recipient", "templateKey" })
        {
            Assert.Contains($"name=\"{field}\"", html);
        }
    }

    /// <summary>
    /// A state trigger has no delay to set, so its form must not offer an hours field — one that does
    /// nothing is worse than none, because somebody sets it and expects an effect.
    /// </summary>
    [Fact]
    public async Task TheRegistrationTriggersFormOffersNoHoursField()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        Assert.DoesNotContain($"hours-{MessageTrigger.CandidateRegistered}", html);
        Assert.Contains($"hours-{MessageTrigger.BeforeSessionStart}", html);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    /// <summary>
    /// The schedule is editable on the template editor itself, because writing the wording and
    /// deciding when it goes are one job — making it two screens was the first thing to trip somebody
    /// up in testing.
    /// </summary>
    [Fact]
    public async Task TheTemplateEditorCanRescheduleTheRuleThatSendsIt()
    {
        using var factory = new WebAppFactory();
        var ruleId = await SeedRuleAsync(factory);

        int templateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            templateId = await db.EmailTemplates.Where(t => t.Key == "DayBeforeReminder").Select(t => t.Id).FirstAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var editUrl = $"/Admin/EmailTemplateEdit/{templateId}";

        // The form is there, carrying the rule's current value rather than a blank.
        var page = await client.GetStringAsync(editUrl);
        Assert.Contains("name=\"parameterHours\"", page);
        Assert.Contains("value=\"24\"", page);

        var response = await client.PostAsync($"{editUrl}?handler=Schedule", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("ruleId", ruleId.ToString()),
            new KeyValuePair<string, string>("parameterHours", "48"),
            new KeyValuePair<string, string>("recipient", "0"),
            new KeyValuePair<string, string>("__RequestVerificationToken", await AntiforgeryTokenAsync(client, editUrl))
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var verify = factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(48, (await db2.MessageRules.FirstAsync(r => r.Id == ruleId)).ParameterHours);
    }

    /// <summary>
    /// Copying a rule is how you get a second reminder at a different hour without retyping the
    /// template, recipient and channel. The copy starts switched off and stamped now, so it cannot
    /// reach anybody whose moment has already passed — see <c>MessageRuleAdminService.DuplicateAsync</c>.
    /// </summary>
    [Fact]
    public async Task ARuleCanBeCopied_AndTheCopyStartsOffAndCarriesNoHistory()
    {
        using var factory = new WebAppFactory();
        var ruleId = await SeedRuleAsync(factory, hours: 24);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var listUrl = $"/Admin/MessageRules?teamId={factory.Seeded.TeamId}";

        var response = await client.PostAsync($"{listUrl}&handler=Duplicate", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("ruleId", ruleId.ToString()),
            new KeyValuePair<string, string>("__RequestVerificationToken", await AntiforgeryTokenAsync(client, listUrl))
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        // Scoped to the pair under test: the factory's seeded team already carries the four default
        // rules, so a bare count would be counting those too.
        var original = await db.MessageRules.FirstAsync(r => r.Id == ruleId);
        var copy = await db.MessageRules.SingleAsync(r => r.Name == "Day before (copy)");

        Assert.Equal(original.Trigger, copy.Trigger);
        Assert.Equal(original.ParameterHours, copy.ParameterHours);
        Assert.Equal(original.TemplateKey, copy.TemplateKey);
        Assert.Equal(original.Recipient, copy.Recipient);
        // Off, so a duplicate made in order to edit does not start sending the moment it exists.
        Assert.False(copy.IsEnabled);
        // Its own clock, so it cannot reach anybody the original has already passed — a copy of a
        // year-old rule would otherwise inherit a year-old bound.
        Assert.True(copy.CreatedUtc > original.CreatedUtc);
        Assert.Empty(db.MessageRuleRuns.Where(r => r.MessageRuleId == copy.Id));
    }

    /// <summary>
    /// Authorized against the rule's own team, never a posted one — a TeamAdmin whose own team is
    /// valid must not be able to reach another team's rule by id (#238).
    /// </summary>
    [Fact]
    public async Task AnotherTeamsRule_CannotBeOpenedForEditing()
    {
        using var factory = new WebAppFactory();
        int otherTeamRuleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The harness's role header only changes the *claim*; CanManageTeam reads the stored row.
            // Without this the seeded user is still a SystemAdmin and the check it is meant to fail
            // passes for the wrong reason — which is exactly how a scope test quietly proves nothing.
            var actingUser = await db.Users.FirstAsync();
            actingUser.Role = UserRole.TeamAdmin;

            var otherTeam = new Team { Name = "OTHERTEAM", CreatedUtc = DateTime.UtcNow };
            db.Teams.Add(otherTeam);
            await db.SaveChangesAsync();

            var rule = new MessageRule
            {
                TeamId = otherTeam.Id,
                Name = "Theirs",
                Trigger = MessageTrigger.BeforeSessionStart,
                ParameterHours = 24,
                TemplateKey = "DayBeforeReminder",
                CreatedUtc = DateTime.UtcNow
            };
            db.MessageRules.Add(rule);
            await db.SaveChangesAsync();
            otherTeamRuleId = rule.Id;
        }

        var client = factory.CreateClientAs(UserRole.TeamAdmin);
        var response = await client.GetAsync($"/Admin/MessageRuleEdit/{otherTeamRuleId}");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Redirect,
            $"A TeamAdmin reached another team's rule: {response.StatusCode}");
    }
}
