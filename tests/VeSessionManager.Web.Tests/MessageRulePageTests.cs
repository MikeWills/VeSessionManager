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

        var rule = new MessageRule
        {
            TeamId = factory.Seeded.TeamId,
            Name = "Day before",
            Trigger = trigger,
            ParameterHours = hours,
            Subject = "Subject",
            Body = templateKey,
            IsEnabled = enabled,
            CreatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.MessageRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    // ⚠️ Four facts were deleted here on 2026-08-21, with the template/rule split:
    // ArrivingFromATemplate_CopiesItsWordsIntoTheNewMessage,
    // ArrivingWithATemplateIdThatIsNotOurs_SelectsNothing,
    // TheTemplateEditor_WithNoRule_LinksStraightToTheCreateForm, and AVeTemplate_IsNotOfferedARule.
    // All four asserted on arriving at the create form carrying a template (#409) — the picker, the
    // ?templateId= link, and which templates were eligible for one.
    //
    // Nothing replaces them, and the reason is not that the coverage moved: a message owns its own
    // Subject/Body now, and the tags it may use depend on its trigger. So a message is authored
    // against its trigger, and there is no separate thing to arrive "from" — no template list, no
    // link carrying one, and no audience rule deciding which may be attached to a rule.

    // ⚠️ TheTemplatesList_OffersToAddARule_CarryingTheTemplate_EvenWhenOneExists was deleted here on
    // 2026-08-21. It asserted the Email Templates list offered a way to add a second rule for a
    // template, carrying that template with it (#409).
    //
    // A message owns its own words now, so nothing points at a template and the templates list has no
    // rules to offer. Making a second message is copying an existing one and changing its timing,
    // which MessageRuleAdminService.DuplicateAsync already does and has its own tests.


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
        Assert.Contains("No messages — nothing is sent at this point.", html);
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
        Assert.Contains("Delete message", html);
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

    // ⚠️ ARulePointingAtAMissingTemplate_IsFlaggedOnTheRow was deleted here on 2026-08-21. It
    // asserted the rules list flagged a rule whose template had been deleted out from under it —
    // otherwise it failed nightly with only a log line. There is no separate template to delete now,
    // so the row cannot get into that state.


    /// <summary>
    /// The create form's field names are what the handler binds. A mismatch binds a default silently,
    /// and only reading the markup catches it (#144).
    /// </summary>
    /// <summary>
    /// <b>The delay field is denominated in days, and the browser has to enforce the halves.</b>
    /// Asserted against the rendered attributes rather than the model, because <c>min</c>/<c>max</c>/
    /// <c>step</c> are the whole of the client-side contract: lose <c>step</c> and the box silently
    /// accepts 0.3, which the server then refuses with an error the person cannot act on, having typed
    /// something the form appeared to allow.
    ///
    /// <para>Both screens, because they are separate markup that has already drifted once — the create
    /// form and the edit form were written days apart.</para>
    /// </summary>
    [Theory]
    [InlineData("new")]
    [InlineData("edit")]
    public async Task TheDelayFieldIsInDays_WithHalvesAllowed(string screen)
    {
        using var factory = new WebAppFactory();
        var ruleId = await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var url = screen == "new"
            ? $"/Admin/MessageRuleNew?teamId={factory.Seeded.TeamId}"
            : $"/Admin/MessageRuleEdit/{ruleId}";
        var html = await client.GetStringAsync(url);

        Assert.Matches("name=\"ParameterDays\"", html);
        Assert.Matches("min=\"0.5\"", html);
        Assert.Matches("max=\"365\"", html);
        Assert.Matches("step=\"0.5\"", html);
        // The label has to say days too. An input that takes days under a label reading "Hours" is
        // the same bug as storing the wrong unit, arriving by a different route.
        Assert.Contains("Days before the session starts", html);
        Assert.DoesNotContain("Hours before the session starts", html);
    }

    /// <summary>
    /// The stored hours come back as days on the edit form — 24 reads as 1, not as 24. This is the
    /// half of the round trip the POST test cannot see: that test proves days go in correctly, this
    /// one proves the box is not showing hours in a field labelled days.
    /// </summary>
    [Fact]
    public async Task TheEditFormShowsStoredHoursAsDays()
    {
        using var factory = new WebAppFactory();
        var ruleId = await SeedRuleAsync(factory, hours: 36);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleEdit/{ruleId}");

        // 36 hours is a day and a half.
        Assert.Matches("name=\"ParameterDays\"[^>]*value=\"1.5\"|value=\"1.5\"[^>]*name=\"ParameterDays\"", html);
    }

    [Fact]
    public async Task TheCreateFormPostsTheNamesTheHandlerBinds()
    {
        using var factory = new WebAppFactory();
        await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleNew?teamId={factory.Seeded.TeamId}");

        foreach (var field in new[] { "TeamId", "Trigger", "Name", "ParameterDays", "Recipient", "Subject", "Body", "Channel", "FanOut" })
        {
            Assert.Contains($"name=\"{field}\"", html);
        }
    }

    /// <summary>
    /// <b>The trigger is a real field, not a hidden one.</b> It used to be fixed by which section's
    /// "+ Add rule" you pressed and named only in a modal heading, which read as "I cannot choose the
    /// trigger" to the person who commissioned the feature — twice. Every trigger point is offered.
    /// </summary>
    [Fact]
    public async Task TheCreateFormLetsYouPickAnyTriggerPoint()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleNew?teamId={factory.Seeded.TeamId}");

        Assert.Contains("id=\"triggerPicker\"", html);
        foreach (var definition in MessageTriggerDefinitions.All)
        {
            Assert.Contains(MessageTriggerLabels.Label(definition.Trigger), html);
        }
    }

    /// <summary>
    /// The section you pressed "+ Add rule" in still decides what the picker opens on — it is a
    /// default now rather than a decision, which is the whole difference.
    /// </summary>
    [Fact]
    public async Task TheTriggerYouCameFrom_IsPreselected()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync(
            $"/Admin/MessageRuleNew?teamId={factory.Seeded.TeamId}&trigger={(int)MessageTrigger.LicenseGranted}");

        Assert.Matches($"value=\"{(int)MessageTrigger.LicenseGranted}\"[^>]*selected", html);
    }

    /// <summary>
    /// A state trigger has no delay to set, so the delay field starts hidden — one that does nothing
    /// is worse than none, because somebody sets it and expects an effect. The script re-applies this
    /// on change; the server ignores a stray value either way.
    /// </summary>
    [Fact]
    public async Task TheDelayFieldIsHiddenForAStateTrigger_AndShownForATimeRelativeOne()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var url = $"/Admin/MessageRuleNew?teamId={factory.Seeded.TeamId}&trigger=";

        var stateTrigger = await client.GetStringAsync($"{url}{(int)MessageTrigger.CandidateRegistered}");
        var timeRelative = await client.GetStringAsync($"{url}{(int)MessageTrigger.BeforeSessionStart}");

        Assert.Matches("id=\"delayField\"[^>]*hidden", stateTrigger);
        Assert.DoesNotMatch("id=\"delayField\"[^>]*hidden", timeRelative);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    // ⚠️ TheTemplateEditorCanRescheduleTheRuleThatSendsIt was deleted here on 2026-08-21. The template
    // editor carried a panel for rescheduling whichever rule sent that template; a template no longer
    // has a rule, and the editing happens on the message itself.


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
        Assert.Equal(original.Body, copy.Body);
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
                Subject = "Subject",
            Body = "DayBeforeReminder",
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
