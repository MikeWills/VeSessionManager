using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Turns "your account no longer exists" from a 500 into a sign-out and a trip back to the login
/// page.
///
/// <para><b>The situation.</b> A cookie can outlive the account it names — the row is deleted or the
/// database is restored to an older state, while the browser still holds a perfectly valid,
/// correctly-signed cookie. Authorization is satisfied (the principal is authenticated), so the page
/// runs, looks the user up, gets null, and throws. Twenty-two call sites across fourteen pages did
/// exactly that, each spelling out the same throw; they now share
/// <c>CurrentUserLoader.GetRequiredUserAsync</c> (#307), which throws in one place instead of
/// twenty-two. <b>This filter is still what makes that throw unreachable</b> — the helper is the
/// backstop, not the handling.
/// The person sees a 500 for something that is not their fault and that they cannot fix — the one
/// thing that would help, signing out, is what the error page does not offer.</para>
///
/// <para><b>Why a filter rather than nineteen edits.</b> Those nineteen sit in three different method
/// shapes, including inside a <c>ContinueWith</c>, so editing them individually is both more churn
/// and easy to leave half-done. Resolving it once, before any handler runs, makes every one of those
/// throws unreachable — they stay as assertions rather than becoming user-visible behaviour, and a
/// twentieth added tomorrow is covered without anyone remembering to.</para>
///
/// <para><b>It costs no extra query.</b> The lookup goes through
/// <see cref="CurrentUserLoader.GetCachedUserWithManagerAsync"/>, so the page handler and the layout
/// reuse the same load this filter already did.</para>
/// </summary>
public class StaleAuthCookieFilter(
    UserManager<User> userManager,
    AppDbContext dbContext,
    IAuthenticationSchemeProvider schemeProvider,
    ILogger<StaleAuthCookieFilter> logger) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var httpContext = context.HttpContext;

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // A volunteer examiner signed in through the self-service scheme is not an application user
        // and must pass straight through. No scheme check is needed for that: VeSelfServiceAuth
        // deliberately carries its id in "vesm:ve-id" rather than NameIdentifier — precisely so a VE
        // id can never resolve as a User id — so GetUserId returns null here and this returns early.
        // If that claim type ever changes, this becomes wrong, which is why the reasoning is written
        // down rather than left implied.
        if (userManager.GetUserId(httpContext.User) is null)
        {
            await next();
            return;
        }

        var user = await userManager.GetCachedUserWithManagerAsync(dbContext, httpContext, httpContext.User);
        if (user is not null)
        {
            await next();
            return;
        }

        logger.LogWarning(
            "Signing out a cookie for user id {UserId}, which no longer exists — the account was deleted or the database was restored beneath it",
            userManager.GetUserId(httpContext.User));

        // Only sign out a scheme that is actually registered. The integration-test harness replaces
        // Identity's cookie with its own scheme, and SignOutAsync on an unregistered scheme throws —
        // which would turn this fix into the very 500 it exists to prevent, in the one environment
        // that would notice.
        if (await schemeProvider.GetSchemeAsync(IdentityConstants.ApplicationScheme) is not null)
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }

        // returnUrl so the trip back is not lost, matching what the authorization challenge itself
        // would have produced had the cookie been rejected a moment earlier.
        var returnUrl = httpContext.Request.Path + httpContext.Request.QueryString;
        context.Result = new RedirectToPageResult("/Account/Login", new { returnUrl });
    }
}
