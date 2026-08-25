using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Editing one message rule (#401, PR2). A page of its own rather than a modal on the list: the hours
/// field is the one thing on this screen somebody will get wrong, and it deserves the trigger's own
/// explanation next to it rather than a tooltip.
///
/// <para>Authorization is against the rule's <b>own</b> team, never a posted one — same re-check
/// EmailTemplateEdit does, and for the same reason (#238).</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class MessageRuleEditModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    MessageRuleAdminService messageRuleAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    /// <summary>The subject line. The message owns its own words now — see <see cref="Body"/>.</summary>
    [BindProperty]
    public string Subject { get; set; } = "";

    /// <summary>
    /// The message itself, authored here rather than in a separate template.
    ///
    /// <para>That split was what made the available tags unanswerable: they depend on the trigger,
    /// and a template had none. Authoring against the trigger is what lets this page list them.</para>
    /// </summary>
    [BindProperty]
    public string Body { get; set; } = "";

    /// <summary>Days on the form, hours in the column — see <see cref="MessageDelay"/>. Halves are legal, so not an <c>int</c>.</summary>
    [BindProperty]
    public decimal? ParameterDays { get; set; }

    /// <summary>
    /// Days or hours. Hours exist for #116 — an hour before a session, which a day-denominated
    /// field could not express without a 12-hour floor. Defaults to days, which is how a team
    /// usually thinks about a reminder.
    /// </summary>
    [BindProperty]
    public MessageDelayUnit ParameterUnit { get; set; } = MessageDelayUnit.Days;

    [BindProperty]
    public MessageRecipient Recipient { get; set; }

    [BindProperty]
    public MessageChannel Channel { get; set; }

    [BindProperty]
    public ulong? DiscordChannelId { get; set; }

    [BindProperty]
    public MessageFanOut FanOut { get; set; }

    [BindProperty]
    public MessageReplyToSource ReplyToSource { get; set; }

    [BindProperty]
    public string? ReplyToOverride { get; set; }

    [BindProperty]
    public string? CcAddress { get; set; }

    [BindProperty]
    public string? BccAddress { get; set; }

    [BindProperty]
    public bool MonitoringCopyOncePerRun { get; set; }

    public MessageRule Rule { get; private set; } = null!;
    /// <summary>
    /// The list this page was opened from, filters and all. Bound from the query string, so it is
    /// never used without <see cref="SafeReturnUrl"/> validating it.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "return")]
    public string? ReturnUrl { get; set; }


    /// <summary>
    /// The team's own reply-to address, named on screen rather than described.
    ///
    /// <para>"Your team's reply-to address" does not say <i>what</i> that is, so the option cannot be
    /// checked against what somebody expects — which is exactly the doubt Mike raised looking at the
    /// form. Null when no EmailSettings row exists yet, which the caption then says plainly instead of
    /// implying an address is configured.</para>
    /// </summary>
    public string? TeamReplyToAddress { get; private set; }

    public MessageTriggerDefinition Definition => MessageTriggerDefinitions.For(Rule.Trigger);
    public bool TakesParameter => Definition.Mechanism == MessageTriggerMechanism.TimeRelative;
    public string TriggerLabel => MessageTriggerLabels.Label(Rule.Trigger);
    public string TriggerBlurb => MessageTriggerLabels.Blurb(Rule.Trigger);
    public string ParameterPrompt => MessageTriggerLabels.ParameterPrompt(Rule.Trigger);

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        Name = Rule.Name;
        Subject = Rule.Subject;
        Body = Rule.Body;
        // Reopened in the unit it was saved in, via ForDisplay — otherwise a rule set to 1 hour comes
        // back as the nearest half-day and saving the form silently moves it (#116).
        var display = MessageDelay.ForDisplay(Rule.ParameterHours);
        ParameterDays = display?.Value;
        ParameterUnit = display?.Unit ?? MessageDelayUnit.Days;
        Recipient = Rule.Recipient;
        Channel = Rule.Channel;
        DiscordChannelId = Rule.DiscordChannelId;
        FanOut = Rule.FanOut;
        ReplyToSource = Rule.ReplyToSource;
        ReplyToOverride = Rule.ReplyToOverride;
        CcAddress = Rule.CcAddress;
        BccAddress = Rule.BccAddress;
        MonitoringCopyOncePerRun = Rule.MonitoringCopyOncePerRun;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (!MessageDelayField.TryToHours(ParameterDays, ParameterUnit, out var parameterHours))
        {
            TempData["ErrorMessage"] = MessageDelayField.RangeMessage;
            return RedirectToPage(new { id = Id, @return = ReturnUrl });
        }

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await messageRuleAdminService.UpdateAsync(
            Id, Name, Subject, Body, parameterHours, Recipient, user.Id, HttpContext.RequestAborted,
            Channel, DiscordChannelId, FanOut,
            new MessageEnvelope(ReplyToSource, ReplyToOverride, CcAddress, BccAddress, MonitoringCopyOncePerRun));

        if (result == MessageRuleActionResult.Success)
        {
            TempData["StatusMessage"] = "Rule updated.";
            // Back to the view they came from, filters intact, rather than the unfiltered first page.
        return Redirect(SafeReturnUrl.Or(Url, ReturnUrl, Url.Page("/Admin/MessageRules", new { teamId = Rule.TeamId })!));
        }

        TempData["ErrorMessage"] = result switch
        {
            MessageRuleActionResult.NameRequired => "A rule needs a name — it is what the run log records.",
            MessageRuleActionResult.ParameterRequired => MessageDelayField.RequiredMessage,
            MessageRuleActionResult.ParameterOutOfRange => MessageDelayField.RangeMessage,
            MessageRuleActionResult.RecipientNotLegal => "That trigger cannot send to that recipient.",
            MessageRuleActionResult.MessageRequired => "Give the message a subject and something to say.",
            MessageRuleActionResult.DiscordChannelRequired => "A Discord rule needs a channel id — without one it would post nowhere.",
            MessageRuleActionResult.DigestNeedsAChannel =>
                "A single digest only makes sense on a channel. On email it would mean one message to one address listing everybody else.",
            MessageRuleActionResult.EnvelopeNeedsEmail =>
                "Reply-To, Cc and Bcc only apply to email — nobody is addressed on a Discord post.",
            MessageRuleActionResult.ReplyToRequired => "Pick an address for replies, or choose one of the other two options.",
            _ => "Rule not found."
        };
        return RedirectToPage(new { id = Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var rule = await dbContext.MessageRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == Id, HttpContext.RequestAborted);
        if (rule is null)
        {
            return NotFound();
        }

        if (!adminAccessScope.CanManageTeam(user, rule.TeamId))
        {
            return Forbid();
        }

        Rule = rule;
        TeamReplyToAddress = await dbContext.EmailSettings
            .AsNoTracking()
            .Where(e => e.TeamId == rule.TeamId)
            .Select(e => e.ReplyToAddress)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        return null;
    }
}
