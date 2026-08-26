using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace VeSessionManager.Web;

/// <summary>
/// Marks a list page whose filters should survive navigating away and back (#459).
///
/// <para>On the page model class. One line, no per-page restore logic — see
/// <see cref="RememberFiltersPageFilter"/> for how it works and why it is done this way.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RemembersFiltersAttribute : Attribute;

/// <summary>
/// Remembers each list page's filters between visits, and puts them back.
///
/// <para>Mike, 2026-08-24: <i>"Ensure that all table filters and sorting is saved so it sticks when
/// navigating back to that page. I know some do not currently save."</i> Every list page read its
/// filters from the query string only, so leaving the page dropped them. Exactly one page had a fix —
/// the sessions list, with its own cookie — and this generalises the idea without generalising the
/// code.</para>
///
/// <para><b>Sorting was already fine.</b> The client-side sorter in <c>app.js</c> keeps each table's
/// column and direction in localStorage, per page and table. This is only about filters.</para>
///
/// <h3>⚠️ Why a redirect rather than restoring values into each page model</h3>
/// <para>The obvious implementation — read the cookie in each <c>OnGetAsync</c> and assign the
/// remembered values back onto the bound properties — means writing per-page restore code fifteen
/// times, each needing to know that page's own defaults, its own key names and its own types. That is
/// fifteen chances to get a default subtly wrong, on pages nobody would think to re-test.</para>
/// <para>This instead redirects a bare visit to the same page <i>with the remembered query string</i>.
/// The page then binds exactly as if the filters had just been submitted, through its own existing
/// code. Nothing about any page model changes, so no page can be restored incorrectly — it either
/// gets its own filters or nothing at all.</para>
///
/// <h3>The rule</h3>
/// <list type="bullet">
/// <item>GET with any query string → those filters win, and the query string is remembered.</item>
/// <item>GET with none, and something remembered → redirect to it.</item>
/// <item>GET with none, nothing remembered → the page's own defaults, unchanged.</item>
/// </list>
///
/// <para>No loop is possible: the redirect always carries a query string, so the request it produces
/// takes the first branch.</para>
///
/// <para>⚠️ <b>A filter form must submit at least one key when everything is cleared</b>, or clearing
/// looks like a bare visit and the old filters come straight back. Text inputs and selects always
/// submit — an emptied search box still sends <c>search=</c> — but a form of nothing but checkboxes
/// would not, which is why <see cref="ClearedMarker"/> exists for those.</para>
/// </summary>
public sealed class RememberFiltersPageFilter(ILogger<RememberFiltersPageFilter> logger) : IAsyncPageFilter
{
    /// <summary>
    /// Cookie holding a small map of page path → remembered query string.
    ///
    /// <para>One cookie at <c>Path=/</c> rather than one per page: fifteen page-scoped cookies would
    /// ride on every request to that area, static assets included.</para>
    /// </summary>
    public const string CookieName = "vsm_filters";

    /// <summary>
    /// What a checkbox-only filter form submits so that "I unticked everything" is a value rather
    /// than an absence. Ignored on the way back in — its presence is the whole message.
    /// </summary>
    public const string ClearedMarker = "f";

    /// <summary>
    /// Well under the ~4KB a browser allows, leaving room for the rest of the request's cookies.
    /// Over the total, browsers start discarding silently — the symptom would be "filters stopped
    /// being remembered everywhere, for no visible reason".
    /// </summary>
    private const int MaxCookieBytes = 3000;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var handlerType = context.HandlerInstance?.GetType();
        var remembers = handlerType?.IsDefined(typeof(RemembersFiltersAttribute), inherit: true) == true;

        // Only a plain GET of the page itself. A named handler (?handler=Something) is an action, not
        // a view of the list, and must never be redirected out from under itself.
        if (!remembers
            || !HttpMethods.IsGet(http.Request.Method)
            || http.Request.Query.ContainsKey("handler"))
        {
            await next();
            return;
        }

        var path = PageKey(http);

        // Not every RemembersFilters page has a team picker (e.g. Audit Log, Job Run History) — only
        // those pages should read or write the cross-page team cookie, or a page with no such filter
        // would pick up a stray, permanently-ignored "teamId" in its own remembered query string.
        var hasTeamFilter = handlerType?.GetProperty("TeamId") is not null;

        if (http.Request.Query.Count > 0)
        {
            Remember(http, path);
            if (hasTeamFilter)
            {
                SharedTeamFilterCookie.RememberIfPresent(http);
            }

            await next();
            return;
        }

        var remembered = ReadAll(http).TryGetValue(path, out var query) ? query : null;

        // The one cross-page value: a team picked on any team-filtered page wins over this page's
        // own last-remembered team, so it does not take a fresh visit here to catch up. Other
        // filters (search, status, ...) stay exactly what this page remembered for itself.
        var sharedTeam = hasTeamFilter ? SharedTeamFilterCookie.Read(http) : null;

        if (!string.IsNullOrWhiteSpace(remembered))
        {
            if (sharedTeam is not null)
            {
                remembered = WithTeamId(remembered, sharedTeam);
            }

            logger.LogDebug("Restoring remembered filters for {Path}", path);
            context.Result = new RedirectResult(http.Request.Path + remembered);
            return;
        }

        // Nothing of this page's own to restore. An empty shared value ("All teams") matches this
        // page's untouched default already, so only a real team id is worth a redirect.
        if (!string.IsNullOrEmpty(sharedTeam))
        {
            context.Result = new RedirectResult(http.Request.Path + WithTeamId("", sharedTeam));
            return;
        }

        await next();
    }

    /// <summary>Replaces (or adds) the <c>teamId</c> key in a query string, preserving every other
    /// key exactly as remembered.</summary>
    private static string WithTeamId(string query, string teamId)
    {
        var pairs = QueryHelpers.ParseQuery(query)
            .Where(kv => kv.Key != SharedTeamFilterCookie.QueryKey)
            .SelectMany(kv => kv.Value, (kv, value) => KeyValuePair.Create<string, string?>(kv.Key, value))
            .Append(KeyValuePair.Create<string, string?>(SharedTeamFilterCookie.QueryKey, teamId))
            .ToList();

        return QueryString.Create(pairs).Value ?? "";
    }

    private void Remember(HttpContext http, string path)
    {
        // The marker is ours, not the page's — storing it would put it back on every restore.
        var pairs = http.Request.Query
            .Where(pair => pair.Key != ClearedMarker)
            .SelectMany(pair => pair.Value.Select(value => (pair.Key, Value: value ?? "")))
            .ToList();

        var all = ReadAll(http);
        all.Remove(path);

        if (pairs.Count > 0)
        {
            var query = QueryString.Create(pairs.Select(p => KeyValuePair.Create<string, string?>(p.Key, p.Value))).Value;
            if (!string.IsNullOrEmpty(query))
            {
                // Re-inserted last, so the page in use is the last one the size guard drops.
                all[path] = query;
            }
        }

        Write(http, all);
    }

    private static string PageKey(HttpContext http) =>
        http.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "/";

    private static Dictionary<string, string> ReadAll(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            // A truncated or hand-edited cookie forgets the filters. It must never fail the request.
            return [];
        }
    }

    private static void Write(HttpContext http, Dictionary<string, string> all)
    {
        var options = BuildOptions(http);
        if (all.Count == 0)
        {
            http.Response.Cookies.Delete(CookieName, options);
            return;
        }

        var value = JsonSerializer.Serialize(all);
        while (value.Length > MaxCookieBytes && all.Count > 1)
        {
            all.Remove(all.Keys.First());
            value = JsonSerializer.Serialize(all);
        }

        if (value.Length > MaxCookieBytes)
        {
            // One page's filters alone blow the budget — a very long search string. Remember nothing
            // rather than write a cookie the browser discards without saying so.
            http.Response.Cookies.Delete(CookieName, options);
            return;
        }

        http.Response.Cookies.Append(CookieName, value, options);
    }

    private static CookieOptions BuildOptions(HttpContext http) => new()
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),

        // ⚠️ Path "/" deliberately. The sessions cookie this generalises was first scoped to
        // "/SessionManager/Index" and was therefore written but never read: Razor Pages trims "Index"
        // from the route, so the nav linked to "/SessionManager" and the browser never matched the
        // path. These pages span /SessionManager and /Admin, so anything narrower repeats that bug.
        Path = "/",
        SameSite = SameSiteMode.Lax,

        // A functional preference the app is not usable as intended without — not analytics.
        IsEssential = true,

        // ⚠️ This can hold typed search text, which on the VE directory or the audit log may be a
        // person's name. HttpOnly keeps it out of reach of any future XSS; it is never logged.
        HttpOnly = true,

        // Conditional, never hardcoded true: that silently breaks local development over
        // http://localhost, where filters would appear to forget themselves with nothing to explain
        // why.
        Secure = http.Request.IsHttps
    };
}
