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

    [BindProperty]
    public string TemplateKey { get; set; } = "";

    [BindProperty]
    public int? ParameterHours { get; set; }

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
    public IReadOnlyList<MessageRulesModel.TemplateOption> Templates { get; private set; } = [];

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
        TemplateKey = Rule.TemplateKey;
        ParameterHours = Rule.ParameterHours;
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

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await messageRuleAdminService.UpdateAsync(
            Id, Name, TemplateKey, ParameterHours, Recipient, user.Id, HttpContext.RequestAborted,
            Channel, DiscordChannelId, FanOut,
            new MessageEnvelope(ReplyToSource, ReplyToOverride, CcAddress, BccAddress, MonitoringCopyOncePerRun));

        if (result == MessageRuleActionResult.Success)
        {
            TempData["StatusMessage"] = "Rule updated.";
            return RedirectToPage("/Admin/MessageRules", new { teamId = Rule.TeamId });
        }

        TempData["ErrorMessage"] = result switch
        {
            MessageRuleActionResult.NameRequired => "A rule needs a name — it is what the run log records.",
            MessageRuleActionResult.ParameterRequired => "This trigger needs a number of hours.",
            MessageRuleActionResult.ParameterOutOfRange =>
                $"Hours must be between 1 and {MessageRuleAdminService.MaxParameterHours} (a year).",
            MessageRuleActionResult.RecipientNotLegal => "That trigger cannot send to that recipient.",
            MessageRuleActionResult.TemplateNotFound => "Pick a template that exists on this team.",
            MessageRuleActionResult.DiscordChannelRequired => "A Discord rule needs a channel id — without one it would post nowhere.",
            MessageRuleActionResult.DigestNeedsAChannel =>
                "A single digest only makes sense on a channel. On email it would mean one message to one address listing everybody else.",
            MessageRuleActionResult.EnvelopeNeedsEmail =>
                "Reply-To, Cc and Bcc only apply to email — nobody is addressed on a Discord post.",
            MessageRuleActionResult.ReplyToRequired => "Pick an address for replies, or choose one of the other two options.",
            MessageRuleActionResult.CcNotAllowedOnCandidateMail =>
                "A Cc on candidate mail cannot unsubscribe and is visible to everyone who gets it. Use Bcc instead.",
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
        Templates = await dbContext.EmailTemplates
            .AsNoTracking()
            .Where(t => t.TeamId == rule.TeamId)
            .OrderBy(t => t.Key)
            .Select(t => new MessageRulesModel.TemplateOption(t.Key, t.DisplayName))
            .ToListAsync(HttpContext.RequestAborted);
        return null;
    }
}
