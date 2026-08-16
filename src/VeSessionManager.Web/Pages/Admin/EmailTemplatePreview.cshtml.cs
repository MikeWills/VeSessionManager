using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// What a template actually looks like when it goes out (#395).
///
/// <para><b>Rendered, not shown as source.</b> The body is HTML with <c>{{Token}}</c> holes in it, so
/// reading the markup tells you very little about what a candidate receives — which is the gap this
/// closes. It is rendered through the real <see cref="EmailTemplateRenderer"/> with sample values, so
/// the preview exercises the same substitution and encoding the send does; a preview with its own
/// renderer would agree with the email right up until it did not.</para>
///
/// <para><b>Sample values, clearly fake.</b> Deliberately not a real candidate: a preview is opened
/// casually and often, and pulling a live person's name and payment link into it turns an idle click
/// into a PII exposure. "Ana Ruiz" is obviously an example.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class EmailTemplatePreviewModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    EmailTemplateRenderer renderer) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public EmailTemplate Template { get; private set; } = null!;
    public string RenderedSubject { get; private set; } = "";
    public string RenderedBody { get; private set; } = "";

    public string Label => Template.DisplayName ?? EmailTemplateLabels.For(Template.Key);

    /// <summary>Tokens the template uses that nothing will ever substitute — the same typo check the editor reports after a save, surfaced where it is visible.</summary>
    public IReadOnlyList<string> UnknownPlaceholders { get; private set; } = [];

    /// <summary>
    /// Stand-in values, one per token this app can substitute anywhere. A single map rather than one
    /// per key: a preview only needs something plausible in the hole, and keeping it in step with
    /// every per-template list would be maintenance with no reader.
    /// </summary>
    private static readonly Dictionary<string, string> SampleValues = new()
    {
        ["CandidateName"] = "Ana Ruiz",
        ["CandidateFirstName"] = "Ana",
        ["VeName"] = "Ana Ruiz",
        ["CallSign"] = "N0EXAMPLE",
        ["SessionDate"] = "10:00 AM ET / 7:00 AM PT on Saturday, March 14",
        ["TeamName"] = "Your Team",
        ["ZoomJoinUrl"] = "https://zoom.us/j/0000000000",
        ["PaymentLinkUrl"] = "https://square.link/u/example",
        ["OutstandingPaymentLinkUrl"] = "https://square.link/u/example",
        ["YouthPaymentLinkUrl"] = "https://example.org/youth-confirm/example",
        ["PrivacyPolicyUrl"] = "https://example.org/privacy",
        ["PaymentAmount"] = "$15.00",
        ["Frn"] = "0001234567",
        ["FccApplicationFileNumber"] = "0012131564",
        ["UnsubscribeUrl"] = "https://example.org/ve/unsubscribe/example"
    };

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == Id, HttpContext.RequestAborted);
        if (template is null)
        {
            return NotFound();
        }

        // The template's own team, never a posted one — same re-check as everywhere else here.
        if (!adminAccessScope.CanManageTeam(user, template.TeamId))
        {
            return Forbid();
        }

        Template = template;

        var rendered = await renderer.RenderTextAsync(
            template.TeamId, template.Subject, template.Body, SampleValues, template.Key, HttpContext.RequestAborted);
        RenderedSubject = rendered.Subject;
        RenderedBody = rendered.Body;

        // The renderer leaves an unknown token as literal text, so it is already visible in the
        // preview — this names them, which is the difference between "why does it say that" and
        // "that is a typo".
        UnknownPlaceholders = [.. System.Text.RegularExpressions.Regex
            .Matches(template.Subject + " " + template.Body, @"\{\{(\w+)\}\}")
            .Select(m => m.Groups[1].Value)
            .Where(name => !SampleValues.ContainsKey(name) && name != "Logo")
            .Distinct()];

        return Page();
    }
}
