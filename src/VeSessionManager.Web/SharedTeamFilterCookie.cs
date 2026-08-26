namespace VeSessionManager.Web;

/// <summary>
/// Remembers the last team picked on any team-filtered page, so picking one on one page carries to
/// every other one (Mike, 2026-08-26: "if I filter on a team on one page, that should carry across
/// to all pages with a team filter" — a TeamAdmin/SessionManager/TeamLead can all sit on more than
/// one team now, so this isn't just a SystemAdmin convenience).
///
/// <para>Deliberately separate from <see cref="RememberFiltersPageFilter"/>'s own per-page cookie.
/// That one preserves a whole page's filter set exactly as it was, keyed by path, so a search typed
/// into VE Directory does not leak onto Unmatched Payments. Team is the one axis several different
/// pages need kept <i>in sync</i> rather than kept separate, which is a different contract — hence a
/// second, single-value cookie rather than teaching the per-page one to treat one key specially.</para>
///
/// <para><b>"All teams" is stored as <see cref="AllTeamsValue"/> ("0"), never an empty string.</b>
/// A cookie value of "" round-tripped unreliably through .NET's own <c>CookieContainer</c> in testing
/// (written, but not read back on the next request) — not worth chasing further when a real team id
/// is never 0 and a non-empty sentinel sidesteps the whole question. An explicit "All teams" pick is
/// still remembered as a real choice, distinguishable from "nothing ever picked" (cookie absent), and
/// still overrides a more specific team remembered earlier on another page.</para>
/// </summary>
public static class SharedTeamFilterCookie
{
    public const string CookieName = "vsm_team_filter";

    /// <summary>Matches the <c>name="teamId"</c> radio inputs in <c>_TeamPicker.cshtml</c> and every
    /// page's own <c>TeamId</c>-bound property (query-string binding is case-insensitive, but this is
    /// the literal key the picker's form actually submits).</summary>
    public const string QueryKey = "teamId";

    /// <summary>Stored/read in place of an empty string — see the class remarks.</summary>
    private const string AllTeamsValue = "0";

    /// <summary>
    /// Call once a page has decided the current request's query string represents a real filter
    /// submission (not a bare navigation). If that query string carries a <c>teamId</c> key — present
    /// on every submission of the shared picker's form, including "All teams" (value "") — remembers
    /// it for every other team-filtered page.
    /// </summary>
    public static void RememberIfPresent(HttpContext http)
    {
        if (!http.Request.Query.TryGetValue(QueryKey, out var values))
        {
            return;
        }

        var value = values.ToString();
        http.Response.Cookies.Append(CookieName, string.IsNullOrEmpty(value) ? AllTeamsValue : value, BuildOptions(http));
    }

    /// <summary>
    /// The last remembered value as it belongs in a query string: null if nothing has ever been
    /// picked on any page, "" if "All teams" was the last explicit pick, otherwise the team id.
    /// </summary>
    public static string? Read(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CookieName, out var raw)
            ? (raw == AllTeamsValue ? "" : raw)
            : null;

    /// <summary>Parses <see cref="Read"/>'s result into the nullable id every page's <c>TeamId</c>
    /// property actually wants — "" and an unparseable value both mean "All teams".</summary>
    public static int? ReadTeamId(HttpContext http) =>
        Read(http) is { Length: > 0 } raw && int.TryParse(raw, out var id) ? id : null;

    private static CookieOptions BuildOptions(HttpContext http) => new()
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        Path = "/",
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        HttpOnly = true,
        Secure = http.Request.IsHttps,
    };
}
