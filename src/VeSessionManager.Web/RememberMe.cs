using Microsoft.AspNetCore.Authentication;

namespace VeSessionManager.Web;

/// <summary>
/// The "Remember me" sign-in, in one place because two sign-in paths use it — password and external
/// (Google/Microsoft) — and a difference between them would show up as "it works when I use my
/// password but not when I use Google", which reads as a broken feature rather than a policy.
///
/// <para><b>Why this exists at all.</b> Every sign-in used to pass <c>isPersistent: false</c>, which
/// writes a cookie with no <c>Max-Age</c> — a session cookie, alive only as long as the browser
/// process. A desktop browser runs for days, so it survived. Phones kill and restart their browser
/// constantly to reclaim memory, and every restart threw the cookie away. The result was a signed-in
/// desktop and a phone that asked for a password almost every time (#340).</para>
///
/// <para><b>Why persistence alone would not have fixed it.</b> With <c>IsPersistent</c> set and
/// nothing else, the cookie takes its lifetime from <c>ExpireTimeSpan</c> — eight hours here, chosen
/// deliberately in #159 because this is an admin backend holding candidate PII. A phone picked up
/// once a day would still be signed out daily. So the window is set explicitly, and only when the
/// box is ticked: an unticked sign-in behaves exactly as it did before.</para>
/// </summary>
internal static class RememberMe
{
    /// <summary>
    /// How long a remembered session survives. Chosen 2026-08-13; the trade is convenience against
    /// how long a lost phone stays signed in, which is why "Sign out other devices" shipped
    /// alongside this rather than after it.
    /// </summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromDays(30);

    /// <summary>
    /// Human-readable window, for the checkbox label. Derived rather than typed, so the words on the
    /// login page cannot say 30 days while the cookie says something else.
    /// </summary>
    internal static string DurationLabel => $"{Duration.TotalDays:0} days";

    /// <summary>
    /// <c>ExpiresUtc</c> is the part that matters and the part that is easy to leave out.
    /// <c>CookieAuthenticationHandler</c> only falls back to <c>ExpireTimeSpan</c> when the ticket
    /// carries no explicit expiry, so setting it here is what buys the longer window — the
    /// <c>isPersistent</c> flag on <c>PasswordSignInAsync</c> cannot express it.
    ///
    /// <para>Sliding expiration keeps working and slides by the <i>ticket's own</i> duration, not by
    /// <c>ExpireTimeSpan</c>: the handler renews using <c>ExpiresUtc - IssuedUtc</c>. So an active
    /// remembered session is extended by 30 days, not silently shortened to eight hours.</para>
    /// </summary>
    internal static AuthenticationProperties Properties(DateTimeOffset now) => new()
    {
        IsPersistent = true,
        ExpiresUtc = now.Add(Duration)
    };

    /// <summary>Key used to carry the checkbox through an external provider's round trip.</summary>
    internal const string ExternalPropertyKey = "vesm:remember_me";
}
