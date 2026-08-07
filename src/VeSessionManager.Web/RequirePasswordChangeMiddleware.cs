using Microsoft.AspNetCore.Identity;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Holds a signed-in user on the Change password page while
/// <see cref="User.MustChangePassword"/> is set — i.e. while they are still using a password an
/// administrator chose for them and typed into a chat message somewhere.
///
/// <para><b>Middleware rather than a page filter</b> so it cannot be forgotten: a new page added
/// later is covered without anyone remembering to attribute it. The cost is that every exemption
/// below has to be deliberate.</para>
/// </summary>
public class RequirePasswordChangeMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Paths that must stay reachable while the redirect is in force.
    ///
    /// <list type="bullet">
    /// <item><c>/Account/</c> — the destination itself, plus Logout: trapping someone on a page they
    /// cannot leave, unable even to sign out, is worse than the risk being managed.</item>
    /// <item><c>/webhooks/</c> — Square posts here unauthenticated; it is never "a user" and must not
    /// be redirected under any circumstances.</item>
    /// <item>Static assets — a redirected CSS request would render the page it lands on unstyled.</item>
    /// </list>
    /// </summary>
    private static readonly string[] ExemptPrefixes =
    [
        "/Account/",
        "/webhooks/",
        "/css/",
        "/js/",
        "/lib/",
        "/favicon"
    ];

    public async Task InvokeAsync(HttpContext context, UserManager<User> userManager)
    {
        if (context.User.Identity?.IsAuthenticated != true || IsExempt(context.Request.Path))
        {
            await next(context);
            return;
        }

        var user = await userManager.GetUserAsync(context.User);
        if (user is not { MustChangePassword: true })
        {
            await next(context);
            return;
        }

        // A POST is redirected too. Letting one through would let a forced user act on the app by
        // submitting a form directly, which is the whole thing being prevented — and 302 on a POST
        // is the correct, if blunt, answer.
        context.Response.Redirect("/Account/ChangePassword");
    }

    private static bool IsExempt(PathString path) =>
        ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)
                                     || path.Value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
}
