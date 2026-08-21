using System.Globalization;
using System.Text.RegularExpressions;
using Discord;

namespace VeSessionManager.Core.Discord;

/// <summary>
/// Which mentions a channel post may resolve, decided per team (#116).
///
/// <para><b>Why an allow-list rather than a switch.</b> Every post this app has ever made went out
/// with <see cref="AllowedMentions.None"/>, and that is precisely what makes
/// <c>DiscordMessageText</c>'s decision <i>not</i> to escape markdown safe: a candidate whose name is
/// <c>@everyone</c> cannot ping the server, because no mention in the message resolves at all. A
/// boolean "allow mentions for this team" hands that guarantee back wholesale — and candidate names
/// reach a channel post through <c>{{Subjects}}</c>, so the hostile string is not hypothetical, it is
/// the ordinary path.</para>
///
/// <para>Naming the roles keeps the guarantee while granting the ask. Only ids a team deliberately
/// listed resolve; <c>@everyone</c> and <c>@here</c> are a separate <see cref="AllowedMentionTypes"/>
/// flag that is never set, whatever the message text says; and user mentions never resolve either.</para>
///
/// <para><b>Verified against the installed package</b> rather than assumed: <c>AllowedMentions</c>'s
/// own documentation states that when <see cref="AllowedMentions.AllowedTypes"/> is null, "only the
/// ids specified in <c>UserIds</c> and <c>RoleIds</c> will be mentioned". Leaving it null is therefore
/// the mechanism, not an oversight — setting any flag would widen it.</para>
/// </summary>
public static partial class DiscordMentionPolicy
{
    /// <summary>
    /// The mention policy for a team's configured roles. Empty or null means the unchanged default:
    /// nothing resolves.
    /// </summary>
    public static AllowedMentions For(IReadOnlyList<ulong>? mentionableRoleIds)
    {
        if (mentionableRoleIds is null || mentionableRoleIds.Count == 0)
        {
            return AllowedMentions.None;
        }

        // AllowedTypes deliberately left null — that is what restricts resolution to these ids alone.
        return new AllowedMentions { RoleIds = [.. mentionableRoleIds] };
    }

    /// <summary>
    /// The role ids in whatever a team typed. Accepts a bare snowflake (what Discord's own "Copy ID"
    /// gives) and the <c>&lt;@&amp;id&gt;</c> mention form (what pasting a role from a message gives),
    /// separated by commas, spaces or newlines.
    ///
    /// <para>⚠️ Anything unparseable is <b>dropped, never guessed at</b>. A malformed entry that
    /// silently became some other id would ping the wrong room of people, and one bad entry does not
    /// discard the good ones beside it.</para>
    /// </summary>
    public static IReadOnlyList<ulong> ParseRoleIds(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var ids = new List<ulong>();
        foreach (Match match in RoleIdPattern().Matches(stored))
        {
            // A snowflake is a 64-bit unsigned integer; anything that does not fit is not one.
            if (ulong.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Matches either <c>&lt;@&amp;123&gt;</c> or a bare run of digits. Deliberately does not match a
    /// word like <c>everyone</c>, which has no id and must never be silently turned into one.
    /// </summary>
    [GeneratedRegex(@"<@&(?<id>\d+)>|(?<id>\d+)")]
    private static partial Regex RoleIdPattern();
}
