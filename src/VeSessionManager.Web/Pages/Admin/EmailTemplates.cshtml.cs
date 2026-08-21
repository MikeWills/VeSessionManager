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

/// <summary>Phase 9c: per-Team EmailTemplate Subject/Body editing, with the available-placeholder chip list per Key. Same team-picker/lock pattern as TeamSettings.</summary>
[Authorize(Roles = RoleGroups.Admins)]
public class EmailTemplatesModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>Label for the team-picker trigger. "Select a team…" rather than "All teams" — this page edits one team's configuration, so there is no merged view to fall back to.</summary>
    public string TeamSummaryLabel { get; private set; } = "Select a team…";

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<TemplateRow> Templates { get; private set; } = [];

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

        TeamId = effectiveTeamId.Value;
        Templates = await dbContext.EmailTemplates
            .Where(t => t.TeamId == effectiveTeamId.Value)
            .OrderBy(t => t.Key)
            .Select(t => new TemplateRow(t.Id, t.Key, t.Subject, t.Body, t.UpdatedUtc, t.IsUserDefined, t.DisplayName, t.Audience))
            .ToListAsync(HttpContext.RequestAborted);

        // What actually sends each template, read from the rules rather than described in prose
        // (#401 PR2). The old grouping was by a hardcoded "phase" per Key, which said "Pre-session"
        // over a template only a button sends and stated conditions — 24 hours, 5 days — that are now
        // a team's own to set.
        SendingRules = (await dbContext.MessageRules
            .AsNoTracking()
            .Where(r => r.TeamId == effectiveTeamId.Value)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.TemplateKey, r.Name, r.Trigger, r.ParameterHours, r.IsEnabled, r.Recipient, r.Channel })
            .ToListAsync(HttpContext.RequestAborted))
            .GroupBy(r => r.TemplateKey)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<SendingRule> (g) => [.. g.Select(r => new SendingRule(
                    r.Id,
                    r.Name,
                    MessageTriggerLabels.Label(r.Trigger),
                    MessageTriggerLabels.DescribeHours(r.ParameterHours),
                    r.IsEnabled,
                    r.Trigger,
                    r.ParameterHours,
                    r.Recipient,
                    r.Channel))]);

        return Page();
    }

    // Creating a template moved to EmailTemplateNew — see there for why it is no longer a form at the
    // bottom of this list.

    /// <summary>Which of this team's rules send each template, keyed by <c>EmailTemplate.Key</c>. Empty for a template no rule references.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SendingRule>> SendingRules { get; private set; } =
        new Dictionary<string, IReadOnlyList<SendingRule>>();

    /// <summary>
    /// Two groups: templates a rule sends, and templates nothing sends on its own.
    ///
    /// <para><b>Read from the rules, not from a hardcoded phase per Key (#401 PR2.)</b> The old
    /// grouping was three phases — "At time of registration", "Pre-session", "Post-session" — assigned
    /// per template in a registry. It lied in two directions at once: "Pre-session" contained a
    /// template only a button ever sends, and the phase said nothing about whether this particular
    /// team had it switched on. The honest split is the one a team can act on.</para>
    ///
    /// <para>A team-defined template lands in the second group by construction — nothing references it
    /// — which is still the right place for it, now for a reason the page can actually check.</para>
    /// </summary>
    public IReadOnlyList<TemplateGroup> GroupedTemplates =>
    [
        new TemplateGroup(
            "Sent automatically",
            "A rule sends these. Change when, or who gets them, on",
            [.. Templates.Where(t => SendingRules.ContainsKey(t.Key))]),
        new TemplateGroup(
            "Not sent by any rule",
            "Nothing sends these on its own — either somebody picks them on a session's \"Email candidates\" screen and edits before sending, or no rule references them yet.",
            [.. Templates.Where(t => !SendingRules.ContainsKey(t.Key))])
    ];

    public IReadOnlyList<SendingRule> RulesFor(string key) =>
        SendingRules.TryGetValue(key, out var rules) ? rules : [];

    public record TemplateGroup(string Label, string Blurb, IReadOnlyList<TemplateRow> Templates);

    /// <param name="Id">So the row can link straight to the rule's editor — "which rule sends this, and let me change it" is one question, not two screens.</param>
    /// <param name="When">"5 days", "immediately" — <c>MessageTriggerLabels.DescribeHours</c>, so the page and the rule cannot disagree.</param>
    public record SendingRule(
        int Id, string Name, string TriggerLabel, string When, bool IsEnabled,
        MessageTrigger Trigger, int? ParameterHours, MessageRecipient Recipient, MessageChannel Channel)
    {
        /// <summary>Whether this rule's trigger has a delay to set. A state trigger has none, so the form shows no delay field rather than one that does nothing.</summary>
        public bool TakesParameter =>
            MessageTriggerDefinitions.For(Trigger).Mechanism == MessageTriggerMechanism.TimeRelative;

        public string ParameterPrompt => MessageTriggerLabels.ParameterPrompt(Trigger);

        /// <summary>The stored hours in the unit the form takes — see <see cref="MessageDelay"/>.</summary>
        public string ParameterDaysText =>
            MessageDelay.ForDisplay(ParameterHours) is { } d ? $"{MessageDelay.Format(d.Value)} {(d.Unit == MessageDelayUnit.Hours ? "hours" : "days")}" : "";

        public IReadOnlyList<MessageRecipient> LegalRecipients => MessageTriggerDefinitions.For(Trigger).LegalRecipients;
    }

    /// <summary>What causes this template to be sent — see EmailTemplateTriggers. Null for a Key with no registry entry, in which case the page shows nothing rather than inventing a description.</summary>
    public static EmailTemplateTrigger? TriggerFor(string key) => EmailTemplateTriggers.For(key);

    /// <summary>
    /// Whether this row is left over from a version that sent it and no longer does. Seeding never
    /// deletes, so the row survives the feature — and an editable template nothing sends is worse
    /// than no template at all, because somebody maintains it and nobody receives it.
    /// </summary>
    public static bool IsRetired(string key) => EmailTemplateTriggers.IsRetired(key);

    /// <param name="DisplayName">Set only for a team's own template; the shipped ones take their label from <c>EmailTemplateLabels</c>, so a name lives in one place rather than in every team's row.</param>
    public record TemplateRow(int Id, string Key, string Subject, string Body, DateTime? UpdatedUtc, bool IsUserDefined, string? DisplayName, EmailTemplateAudience Audience)
    {
        public string Label => DisplayName ?? EmailTemplateLabels.For(Key);
    }
}
