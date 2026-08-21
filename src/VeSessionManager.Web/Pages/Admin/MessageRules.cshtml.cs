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
/// Message Rules (#401, PR2) — what this team sends automatically, and when.
///
/// <para>Every trigger point renders, including the ones with no rules on them. A section that
/// appears only once something is configured is one nobody discovers, and "we could email people at
/// this moment and currently do not" is the most useful thing this page has to say. Same reason the
/// alerts bell renders empty rather than disappearing (#339).</para>
///
/// <para>Same team-picker/lock pattern as TeamSettings and Email Templates: this edits one team's
/// configuration, so there is deliberately no "All teams" option to fall back to.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class MessageRulesModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    MessageRuleAdminService messageRuleAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>
    /// A template key to mark on arrival, so "Rules" on a template row lands on <i>that</i> template's
    /// rules rather than on a list to scan (asked for 2026-08-21).
    ///
    /// <para>Keyed by template rather than by rule id because a template can have several rules and all
    /// of them are the answer to "show me the rules for this". Same convention as the alert bell's
    /// <c>?highlight=</c>: it <b>marks, it never filters</b> — hiding the other rules would answer a
    /// narrower question than the one asked, and a stale key then costs nothing because nothing is
    /// looked up by it.</para>
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? HighlightTemplate { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>"Select a team…" rather than "All teams" — see the class note.</summary>
    public string TeamSummaryLabel { get; private set; } = "Select a team…";

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<TriggerSection> Sections { get; private set; } = [];

    /// <summary>This team's templates, for the create form's picker. A rule can only point at one of these — see MessageRuleAdminService.</summary>
    public IReadOnlyList<TemplateOption> Templates { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AvailableTeams = await adminAccessScope.GetAvailableTeamsAsync(dbContext, user, HttpContext.RequestAborted);

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, AvailableTeams.Select(t => t.Id).ToList());
        // See TeamSettings: keep the picker's rendered state in step with the auto-selection.
        TeamId = effectiveTeamId;
        TeamSummaryLabel = effectiveTeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == effectiveTeamId).Name ?? "Select a team…"
            : "Select a team…";
        if (effectiveTeamId is null)
        {
            return Page();
        }

        await LoadTeamAsync(effectiveTeamId.Value);
        return Page();
    }

    // Creation lives on MessageRuleNew, not here. It used to be a per-section modal posting to a
    // Create handler on this page; that handler went with the modal rather than being left behind,
    // because a second create path taking hours while the form takes days is exactly how the two
    // drift apart unnoticed.

    /// <summary>
    /// Authorized against the <b>rule's own</b> team, never a posted one — a TeamAdmin posting their
    /// own valid teamId alongside another team's ruleId is the cross-tenant hole (#238).
    /// </summary>
    public async Task<IActionResult> OnPostSetEnabledAsync(int ruleId, bool enabled)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var rule = await dbContext.MessageRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, HttpContext.RequestAborted);
        if (rule is null)
        {
            return NotFound();
        }

        if (!adminAccessScope.CanManageTeam(user, rule.TeamId))
        {
            return Forbid();
        }

        var result = await messageRuleAdminService.SetEnabledAsync(ruleId, enabled, user.Id, HttpContext.RequestAborted);
        SetStatus(result, enabled ? "Rule switched on." : "Rule switched off.");
        return RedirectToPage(new { teamId = rule.TeamId });
    }

    /// <summary>Copies a rule, switched off — same authorization as the rest, against the rule's own team.</summary>
    public async Task<IActionResult> OnPostDuplicateAsync(int ruleId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var rule = await dbContext.MessageRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, HttpContext.RequestAborted);
        if (rule is null)
        {
            return NotFound();
        }

        if (!adminAccessScope.CanManageTeam(user, rule.TeamId))
        {
            return Forbid();
        }

        var result = await messageRuleAdminService.DuplicateAsync(ruleId, user.Id, HttpContext.RequestAborted);
        SetStatus(result, "Rule copied. The copy is switched off — edit it, then switch it on.");
        return RedirectToPage(new { teamId = rule.TeamId });
    }

    /// <summary>Same authorization as switching off, against the rule's own team. Confirmed in the browser first — this is not undoable, and the rule does not come back on the next Worker start.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int ruleId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var rule = await dbContext.MessageRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, HttpContext.RequestAborted);
        if (rule is null)
        {
            return NotFound();
        }

        if (!adminAccessScope.CanManageTeam(user, rule.TeamId))
        {
            return Forbid();
        }

        var result = await messageRuleAdminService.DeleteAsync(ruleId, user.Id, HttpContext.RequestAborted);
        SetStatus(result, "Rule deleted.");
        return RedirectToPage(new { teamId = rule.TeamId });
    }

    private async Task LoadTeamAsync(int teamId)
    {
        Templates = await dbContext.EmailTemplates
            .AsNoTracking()
            .Where(t => t.TeamId == teamId)
            .OrderBy(t => t.Key)
            .Select(t => new TemplateOption(t.Key, t.DisplayName))
            .ToListAsync(HttpContext.RequestAborted);

        var rules = await dbContext.MessageRules
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .OrderBy(r => r.Id)
            .ToListAsync(HttpContext.RequestAborted);

        // The template key each rule points at may not exist — a template can be deleted out from
        // under a rule (#144 allows deleting team-defined ones). Surfaced on the row rather than left
        // to fail silently every night with one log line.
        var templateKeys = Templates.Select(t => t.Key).ToHashSet();

        Sections = [.. MessageTriggerDefinitions.All.Select(definition => new TriggerSection(
            definition.Trigger,
            MessageTriggerLabels.Label(definition.Trigger),
            MessageTriggerLabels.Blurb(definition.Trigger),
            [.. rules.Where(r => r.Trigger == definition.Trigger).Select(r => new RuleRow(
                r.Id,
                r.Name,
                r.TemplateKey,
                LabelFor(r.TemplateKey),
                templateKeys.Contains(r.TemplateKey),
                MessageTriggerLabels.DescribeHours(r.ParameterHours),
                DestinationLabel(r),
                r.IsEnabled))]))];
    }

    /// <summary>Where the message goes, which for a Discord rule is a room rather than a person — "The candidate" over a channel post would be actively misleading.</summary>
    private static string DestinationLabel(MessageRule rule) => rule.Channel == MessageChannel.Discord
        ? $"Discord{(rule.FanOut == MessageFanOut.SingleDigest ? " (one digest)" : "")}"
        : MessageTriggerLabels.Label(rule.Recipient);

    private string LabelFor(string key) =>
        Templates.FirstOrDefault(t => t.Key == key) is { } template ? template.Label : key;

    private void SetStatus(MessageRuleActionResult result, string success) =>
        TempData[result == MessageRuleActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            MessageRuleActionResult.Success => success,
            MessageRuleActionResult.NameRequired => "A rule needs a name — it is what the run log records.",
            MessageRuleActionResult.ParameterRequired => MessageDelayField.RequiredMessage,
            MessageRuleActionResult.ParameterOutOfRange => MessageDelayField.RangeMessage,
            MessageRuleActionResult.RecipientNotLegal => "That trigger cannot send to that recipient.",
            MessageRuleActionResult.TemplateNotFound => "Pick a template that exists on this team.",
            MessageRuleActionResult.TemplateAudienceMismatch =>
                "That template is written for VEs. A rule can only send one written for candidates.",
            MessageRuleActionResult.DiscordChannelRequired => "A Discord rule needs a channel id — without one it would post nowhere.",
            MessageRuleActionResult.DigestNeedsAChannel =>
                "A single digest only makes sense on a channel. On email it would mean one message to one address listing everybody else.",
            _ => "Rule not found."
        };

    /// <summary>
    /// One trigger point and whatever rules a team has hung on it, including none. What the form used
    /// to need — the prompt, the ceiling note, the default delay, the legal recipients — moved to
    /// <c>MessageRuleNew</c> with the form itself; this list only reads.
    /// </summary>
    public record TriggerSection(
        MessageTrigger Trigger,
        string Label,
        string Blurb,
        IReadOnlyList<RuleRow> Rules);

    /// <param name="TemplateExists">False when the rule points at a template that is no longer there — the row says so, because otherwise this fails nightly in silence.</param>
    /// <param name="ParameterLabel">"1 day", "half a day" — the same words the form takes, so a rule reads back the way it was written.</param>
    public record RuleRow(
        int Id,
        string Name,
        string TemplateKey,
        string TemplateLabel,
        bool TemplateExists,
        string ParameterLabel,
        string RecipientLabel,
        bool IsEnabled);

    public record TemplateOption(string Key, string? DisplayName)
    {
        public string Label => DisplayName ?? EmailTemplateLabels.For(Key);
    }
}
