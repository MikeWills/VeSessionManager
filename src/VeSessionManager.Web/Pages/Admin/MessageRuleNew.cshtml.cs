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
/// Creating a message rule, with the trigger point as an actual field (#401).
///
/// <para><b>This replaced a modal per trigger point, and the reason is worth keeping.</b> Each
/// section of the Message Rules list had its own "+ Add rule" button opening a modal whose trigger
/// was a hidden input — fixed by which button you pressed, and named only in the modal's heading.
/// The logic was sound and it read as "I cannot choose the trigger", twice, to the person who
/// commissioned the feature. A field somebody can see and change beats a heading that explains why
/// they do not need one.</para>
///
/// <para>The trigger is freely settable <b>here</b> and nowhere else: a rule that has never run has
/// no <c>MessageRuleRun</c> markers to reinterpret, which is exactly what makes changing it unsafe
/// once it exists. See <c>MessageRuleAdminService.UpdateScheduleAsync</c>.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class MessageRuleNewModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    MessageRuleAdminService messageRuleAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int TeamId { get; set; }

    /// <summary>Which section's "+ Add rule" was pressed, so the picker opens on the moment they were looking at. Just a default now, not a decision.</summary>
    [BindProperty(SupportsGet = true)]
    public MessageTrigger Trigger { get; set; } = MessageTrigger.CandidateRegistered;

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string TemplateKey { get; set; } = "";

    /// <summary>
    /// Days, converted to hours on the way in — see <see cref="MessageDelay"/> for why the form and the
    /// column disagree on the unit. Half-days are legal, so this cannot be an <c>int</c>.
    /// </summary>
    [BindProperty]
    public decimal? ParameterDays { get; set; }

    [BindProperty]
    public MessageRecipient Recipient { get; set; } = MessageRecipient.Candidate;

    [BindProperty]
    public MessageChannel Channel { get; set; } = MessageChannel.Email;

    [BindProperty]
    public ulong? DiscordChannelId { get; set; }

    [BindProperty]
    public MessageFanOut FanOut { get; set; } = MessageFanOut.PerRecipient;

    public IReadOnlyList<MessageRulesModel.TemplateOption> Templates { get; private set; } = [];

    public IReadOnlyList<MessageTriggerDefinition> Triggers => MessageTriggerDefinitions.All;

    public static string Label(MessageTrigger trigger) => MessageTriggerLabels.Label(trigger);
    public static string Blurb(MessageTrigger trigger) => MessageTriggerLabels.Blurb(trigger);
    public static string ParameterPrompt(MessageTrigger trigger) => MessageTriggerLabels.ParameterPrompt(trigger);
    public static string? CeilingNote(MessageTrigger trigger) => MessageTriggerLabels.ParameterCeilingNote(trigger);
    public static string Label(MessageRecipient recipient) => MessageTriggerLabels.Label(recipient);

    /// <summary>The trigger's shipped default, in the unit the form takes — "" for a trigger with no delay.</summary>
    public static string DefaultDaysText(MessageTrigger trigger) =>
        DefaultDays(trigger) is { } days ? MessageDelay.Format(days) : "";

    private static decimal? DefaultDays(MessageTrigger trigger) =>
        MessageDelay.ToDays(MessageTriggerDefinitions.For(trigger).DefaultParameterHours);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null || !adminAccessScope.CanManageTeam(user, TeamId))
        {
            return Forbid();
        }

        await LoadTemplatesAsync();

        // Whatever the chosen trigger's default is, so the delay box is never blank on arrival.
        ParameterDays = DefaultDays(Trigger);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null || !adminAccessScope.CanManageTeam(user, TeamId))
        {
            return Forbid();
        }

        if (!MessageDelayField.TryToHours(ParameterDays, out var parameterHours))
        {
            TempData["ErrorMessage"] = MessageDelayField.RangeMessage;
            return RedirectToPage(new { teamId = TeamId, trigger = Trigger });
        }

        var result = await messageRuleAdminService.CreateAsync(
            TeamId, Trigger, Name, TemplateKey, parameterHours, Recipient, user.Id, HttpContext.RequestAborted,
            Channel, DiscordChannelId, FanOut);

        if (result == MessageRuleActionResult.Success)
        {
            TempData["StatusMessage"] = "Rule created.";
            return RedirectToPage("/Admin/MessageRules", new { teamId = TeamId });
        }

        TempData["ErrorMessage"] = result switch
        {
            MessageRuleActionResult.NameRequired => "A rule needs a name — it is what the run log records.",
            MessageRuleActionResult.ParameterRequired => MessageDelayField.RequiredMessage,
            MessageRuleActionResult.ParameterOutOfRange => MessageDelayField.RangeMessage,
            MessageRuleActionResult.RecipientNotLegal => "That trigger cannot send to that recipient.",
            MessageRuleActionResult.TemplateNotFound => "Pick a template that exists on this team.",
            MessageRuleActionResult.DiscordChannelRequired => "A Discord rule needs a channel id — without one it would post nowhere.",
            MessageRuleActionResult.DigestNeedsAChannel =>
                "A single digest only makes sense on a channel. On email it would mean one message to one address listing everybody else.",
            _ => "Rule not created."
        };
        return RedirectToPage(new { teamId = TeamId, trigger = Trigger });
    }

    private async Task LoadTemplatesAsync() =>
        Templates = await dbContext.EmailTemplates
            .AsNoTracking()
            .Where(t => t.TeamId == TeamId)
            .OrderBy(t => t.Key)
            .Select(t => new MessageRulesModel.TemplateOption(t.Key, t.DisplayName))
            .ToListAsync(HttpContext.RequestAborted);
}
