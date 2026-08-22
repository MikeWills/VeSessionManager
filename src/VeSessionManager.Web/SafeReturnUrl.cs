using Microsoft.AspNetCore.Mvc;

namespace VeSessionManager.Web;

/// <summary>
/// Where "back to the list" goes, when the list you came from was filtered.
///
/// <para>A breadcrumb built from a page name alone lands on the unfiltered first page: pick a team,
/// open a message, press back, pick the team again. Mike, 2026-08-21. The list already knows its own
/// URL — <c>Index.BuildPageUrl</c> renders every filter and the page number — so the fix is to carry
/// that forward rather than have each detail page try to reconstruct state it never had.</para>
///
/// <para><b>Validated, never trusted.</b> A return URL arrives in the query string, which anybody can
/// write. <see cref="IUrlHelper.IsLocalUrl"/> is what stops <c>?return=https://evil.example</c>
/// turning an admin breadcrumb into an open redirect — the classic phishing shape, since the link is
/// on a page the victim reached by signing in. Anything not local silently falls back to the plain
/// page, which is the behaviour this replaced and is never wrong, only forgetful.</para>
/// </summary>
public static class SafeReturnUrl
{
    /// <param name="candidate">The <c>return</c> query value, straight off the request.</param>
    /// <param name="fallback">Where to go when there is no usable return URL. Must be app-relative.</param>
    public static string Or(IUrlHelper url, string? candidate, string fallback) =>
        !string.IsNullOrWhiteSpace(candidate) && url.IsLocalUrl(candidate) ? candidate : fallback;
}
