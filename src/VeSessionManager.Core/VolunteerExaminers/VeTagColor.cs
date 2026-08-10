using System.Text.RegularExpressions;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Validation and precedence for <see cref="VeTag.Color"/> (requested 2026-08-09, so a team can be
/// colour-coded on the VE membership screens).
///
/// <para><b>Why validation is not optional here.</b> A tag colour is the first user-supplied value in
/// this app that gets written into a CSS context — <c>style="--tag-color: …"</c>. Razor HTML-encodes
/// the attribute, which stops it escaping into markup, but it does <i>not</i> stop it escaping into
/// the *stylesheet*: a stored value of <c>red; background-image: url(https://evil/x)</c> is perfectly
/// valid HTML and would be honoured as CSS, and the app's own CSP allows inline styles
/// (<c>style-src 'unsafe-inline'</c>), so nothing downstream would block it. So the value is pinned
/// to exactly <c>#RRGGBB</c> — the one shape <c>&lt;input type="color"&gt;</c> ever produces — and
/// checked in BOTH directions: on write, and again on render via <see cref="ForStyle"/>. The second
/// check is what covers a row that reached the database some other way (an import, a hand-edited
/// SQLite file, a future bulk update that forgets).</para>
/// </summary>
public static partial class VeTagColor
{
    /// <summary>Six-digit hex only. Three-digit shorthand, <c>rgb()</c> and named colours are all rejected rather than normalised — the picker only ever emits this shape, so anything else arriving is a bug or an attack, and neither deserves a best-effort interpretation.</summary>
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public static bool IsValid(string? color) => color is not null && HexColor().IsMatch(color);

    /// <summary>
    /// Normalises what a form posted into what should be stored: null for "no colour" (an empty
    /// field, or the picker left unused), lower-case hex otherwise. Returns false for anything that
    /// is neither, so the caller can reject rather than silently drop the value.
    /// </summary>
    public static bool TryNormalize(string? posted, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(posted))
        {
            normalized = null;
            return true;
        }

        posted = posted.Trim();
        if (!IsValid(posted))
        {
            normalized = null;
            return false;
        }

        normalized = posted.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// The value to interpolate into a <c>style</c> attribute, or null if there is nothing safe to
    /// render. Every view MUST go through this rather than reading <c>tag.Color</c> directly — see
    /// the class remarks for why the write-side check alone is not enough.
    /// </summary>
    public static string? ForStyle(string? color) => IsValid(color) ? color : null;

    /// <summary>
    /// Which colour represents a whole membership: <b>the highest-priority tag that has one</b>.
    ///
    /// <para>"Highest" is the tag shown first, which is the <i>lowest</i> <see cref="VeTag.SortOrder"/>
    /// — the same order the VE screens already list tags in, and the same field the Add/Edit form
    /// calls "Order — lower shows first". Worth stating plainly because "highest tag wins" and
    /// "lowest number wins" describe the same rule and read like opposites.</para>
    ///
    /// <para>Tags without a colour are skipped rather than losing outright: a team whose top tag is
    /// uncoloured still gets coded by the next one down, which is what someone colouring only their
    /// "Team lead" tag actually expects.</para>
    /// </summary>
    public static string? ForTags(IEnumerable<VeTag> tags) =>
        tags.Where(t => IsValid(t.Color))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Color!.ToLowerInvariant())
            .FirstOrDefault();
}
