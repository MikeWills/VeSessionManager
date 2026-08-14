using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Carries a half-authenticated user from the password step to the TOTP challenge (#356).
///
/// <para><b>Why this is hand-rolled rather than <c>PasswordSignInAsync</c>.</b> Identity's own flow
/// stores this state for you, but only from <c>PasswordSignInAsync</c> — which this app deliberately
/// does not use. #340 split password-check from sign-in precisely so the app cookie could be issued
/// once, with an explicit 30-day lifetime that <c>isPersistent</c> cannot express. Going back to
/// <c>PasswordSignInAsync</c> would reintroduce the double <c>Set-Cookie</c> that fix exists to
/// avoid, and calling it purely to establish this cookie would verify the password a second time.</para>
///
/// <para><b>The shape is Identity's, and that is a contract worth pinning.</b>
/// <c>SignInManager.GetTwoFactorAuthenticationUserAsync</c> reads the
/// <c>TwoFactorUserIdScheme</c> cookie and expects the user id in a <see cref="ClaimTypes.Name"/>
/// claim — the same thing Identity's internal <c>StoreTwoFactorInfo</c> writes. That is behaviour,
/// not documentation, so <c>TwoFactorSignInTests</c> asserts the round trip rather than trusting it:
/// exactly the standard applied to MimeKit's header handling in #261.</para>
///
/// <para>The cookie is short-lived and carries no roles or identity beyond the id. It is not an
/// authenticated session: nothing in the app authorises against this scheme, so holding one grants
/// access to nothing but the challenge page.</para>
/// </summary>
public static class TwoFactorSignIn
{
    /// <summary>
    /// Long enough to fetch a phone, short enough that an abandoned half-sign-in on a shared machine
    /// does not sit there all afternoon. Identity's own default for this cookie is a session cookie;
    /// an explicit bound is stricter.
    /// </summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(10);

    /// <summary>Records "this user proved their password and now owes a code".</summary>
    public static Task BeginAsync(HttpContext context, User user, DateTimeOffset now)
    {
        var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString()));

        return context.SignInAsync(
            IdentityConstants.TwoFactorUserIdScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                IssuedUtc = now,
                ExpiresUtc = now.Add(PendingWindow)
            });
    }

    /// <summary>
    /// Clears the pending state. Called on success and on abandonment — leaving it behind would let
    /// a later visit to the challenge page resume a sign-in whose password step happened arbitrarily
    /// long ago.
    /// </summary>
    public static Task EndAsync(HttpContext context) =>
        context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

    /// <summary>
    /// Whether this browser has already satisfied a challenge recently enough to be trusted (the
    /// "remember this device" cookie). Checked before challenging, so a trusted device goes straight
    /// through.
    /// </summary>
    public static Task<bool> IsDeviceRememberedAsync(SignInManager<User> signInManager, User user) =>
        signInManager.IsTwoFactorClientRememberedAsync(user);
}
