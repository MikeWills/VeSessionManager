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
