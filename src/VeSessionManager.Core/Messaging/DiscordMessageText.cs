using System.Net;
using System.Text.RegularExpressions;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// Turns a rendered email template into something worth reading in Discord (#401 PR4).
///
/// <para><b>Why this has to exist at all.</b> Templates are HTML, because every other channel this
/// app has is email. Discord renders none of it — posting a template raw puts <c>&lt;p&gt;</c> and
/// <c>&lt;strong&gt;</c> in front of whoever is in the room. A team could of course write a
/// Discord-only template in plain text, and that works too: this leaves text without markup
/// untouched.</para>
///
/// <para><b>Deliberately small, and deliberately not a parser.</b> The input is this app's own
/// templates, not arbitrary web pages — a handful of block tags, bold, and links. A real HTML parser
/// would be a dependency and a lot of behaviour to reason about for a job whose whole output is
/// "readable in a chat window". What it does not recognise it strips, which is the safe failure:
/// text with a tag missing still reads, text with tags in it does not.</para>
///
/// <para><b>It does not escape Discord markdown, deliberately.</b> The obvious next step — backslash
/// every <c>_</c>, <c>~</c>, <c>`</c> so a candidate's name cannot italicise the rest of the post —
/// breaks the thing these posts are mostly for: an underscore inside a URL is common, and escaping it
/// leaves a visible backslash and a dead link. The cost of not escaping is a mangled line in the
/// team's own channel; the cost of escaping is broken links in every message. Markdown is also not
/// executable, so unlike the HTML case (#260) there is nothing to inject.</para>
///
/// <para><b>The one exception is handled elsewhere and properly.</b> A candidate calling themselves
/// <c>@everyone</c> would ping the whole server, which is a real consequence rather than a cosmetic
/// one — so <c>DiscordEventClient.PostMessageAsync</c> posts with <c>AllowedMentions.None</c>, and no
/// mention in any message resolves regardless of what the text says. A control at the API is worth
/// more than string-mangling that has to anticipate every syntax.</para>
/// </summary>
public static partial class DiscordMessageText
{
    /// <summary>Discord rejects a message over 2000 characters outright, so a long template would fail the whole post rather than arrive truncated.</summary>
    public const int MaxLength = 2000;

    public static string FromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var text = html;

        // Bold and emphasis survive as Discord markdown; everything else becomes plain text.
        text = BoldTag().Replace(text, "**$1**");
        text = ItalicTag().Replace(text, "*$1*");

        // A link becomes "text (url)" rather than dropping either half — Discord auto-links a bare
        // URL, and the anchor text is usually the sentence that makes it make sense.
        text = AnchorTag().Replace(text, m =>
        {
            var url = m.Groups["href"].Value.Trim();
            var label = StripTags(m.Groups["label"].Value).Trim();
            if (url.Length == 0) return label;
            // A link whose text is already the URL reads as "https://x (https://x)" otherwise.
            return label.Length == 0 || label.Equals(url, StringComparison.OrdinalIgnoreCase) ? url : $"{label} ({url})";
        });

        // A list item is the one structure worth keeping visibly: the seeded templates use bullets for
        // "what to bring", and a run-on sentence loses that.
        text = ListItemTag().Replace(text, "\n• ");
        text = BlockBoundary().Replace(text, "\n");
        text = StripTags(text);

        text = WebUtility.HtmlDecode(text);

        // Collapse the blank lines the block conversion leaves behind, then trim.
        text = ExcessBlankLines().Replace(text, "\n\n");
        text = TrailingSpaceOnLine().Replace(text, "");
        text = text.Trim();

        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)].TrimEnd() + "…";
    }

    private static string StripTags(string html) => AnyTag().Replace(html, "");

    [GeneratedRegex(@"<\s*(?:b|strong)\s*>(.*?)<\s*/\s*(?:b|strong)\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BoldTag();

    [GeneratedRegex(@"<\s*(?:i|em)\s*>(.*?)<\s*/\s*(?:i|em)\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ItalicTag();

    [GeneratedRegex(@"<\s*a\b[^>]*?href\s*=\s*[""'](?<href>[^""']*)[""'][^>]*>(?<label>.*?)<\s*/\s*a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorTag();

    [GeneratedRegex(@"<\s*li\s*[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTag();

    [GeneratedRegex(@"<\s*/?\s*(?:p|div|br|ul|ol|li|h[1-6]|tr|table)\s*[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundary();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    [GeneratedRegex(@"[ \t]+(?=\n)")]
    private static partial Regex TrailingSpaceOnLine();
}
