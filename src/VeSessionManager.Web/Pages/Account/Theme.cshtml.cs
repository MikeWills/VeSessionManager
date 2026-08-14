using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Saves the chassis theme toggle's choice onto the account, so dark mode follows someone to their
/// phone instead of being remembered per browser (localStorage keeps working alongside it — see
/// wwwroot/js/theme.js for the full resolution order).
///
/// <para>This is the only endpoint in the app that JavaScript posts to. Everything else is a real
/// form submit, which is the right default; a theme toggle is the one control where a full page
/// round trip to change a colour would be worse than the thing it is fixing. The antiforgery token
/// travels as a <c>RequestVerificationToken</c> header rather than the hidden form field, since a
/// <c>fetch()</c> has no form to carry one — that needs no configuration, being
/// <c>AntiforgeryOptions.HeaderName</c>'s default, and Razor Pages validates it on every POST
/// either way.</para>
///
/// <para>Authenticated by the app-wide FallbackPolicy, not by an attribute — there is nothing here a
/// signed-out visitor could usefully do, and a signed-out page never renders the token or the URL in
/// the first place.</para>
/// </summary>
public class ThemeModel(UserManager<User> userManager, AppDbContext dbContext) : PageModel
{
    /// <summary>
    /// There is no page to show. Returning 404 rather than an empty 200 keeps it out of anything that
    /// crawls the app's own routes looking for renderable pages (PageSmokeTests tolerates a 404 for
    /// exactly this case) and says plainly that the URL is not somewhere to navigate.
    /// </summary>
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(string? theme)
    {
        // Only the two explicit values. "System" is the absence of a choice and the toggle never
        // sends it, so accepting it here would give a hand-crafted POST a way into a state the UI
        // cannot reach or display.
        var preference = theme switch
        {
            "dark" => ThemePreference.Dark,
            "light" => ThemePreference.Light,
            _ => (ThemePreference?)null
        };

        if (preference is null)
        {
            return BadRequest();
        }

        var userId = userManager.GetUserId(User);
        if (!int.TryParse(userId, out var id))
        {
            return Unauthorized();
        }

        // ExecuteUpdateAsync rather than loading the user and calling UserManager.UpdateAsync: this
        // is a single scalar stamp on the account making the request, so there is no ownership
        // question to re-check and no reason to run Identity's validators (or touch the security
        // stamp) over a colour scheme.
        await dbContext.Users
            .Where(u => u.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.ThemePreference, preference.Value),
                HttpContext.RequestAborted);

        // No body and nothing to redirect to — the browser has already applied the theme locally and
        // is not waiting on this.
        return new NoContentResult();
    }
}
