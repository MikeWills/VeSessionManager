namespace VeSessionManager.Core.Discord;

/// <summary>
/// Pulls call-sign-shaped tokens out of a Discord display name (#519) — "Mike - WX0MIK",
/// "📻 WX0MIK ⚡", "[VE] wx0mik" all yield <c>WX0MIK</c>.
///
/// <para><b>Candidates, not matches.</b> The shape test (<see cref="CallSign.IsUsable"/>) is loose on
/// purpose: "Ham2" is call-sign-shaped and is nobody's call. The filter that actually decides is the
/// team's own roster — a token means something only if it equals a VE's call sign — so tightening the
/// shape here would buy nothing and would start rejecting real calls, of which the amateur service has
/// a wider variety than any regex a reader would believe.</para>
///
/// <para><b>Nothing is resolved here.</b> Two call signs in one name come back as two candidates and
/// the caller reports it as ambiguous; picking the first would assign one person's tags to another by
/// string order.</para>
/// </summary>
public static class DiscordCallSignParser
{
    /// <summary>
    /// Every distinct call-sign-shaped token in <paramref name="displayName"/>, upper-invariant.
    ///
    /// <para>A portable suffix is split as well as kept: people write themselves "WX0MIK/M" in a
    /// server name and the stored call sign never carries one, so the base call has to be offered in
    /// its own right or the match is lost. Both forms are returned — the roster decides which exists.</para>
    /// </summary>
    public static IReadOnlyCollection<string> Candidates(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.Ordinal);

        // Split on everything that cannot appear in a call sign, which leaves punctuation, emoji and
        // any other decoration out without having to enumerate what people put in a Discord name.
        foreach (var token in Tokenize(displayName))
        {
            Consider(found, token);

            var slash = token.IndexOf('/');
            if (slash > 0)
            {
                Consider(found, token[..slash]);
            }
        }

        return found;
    }

    private static void Consider(HashSet<string> found, string token)
    {
        var normalized = CallSign.NormalizeFormat(token);
        if (normalized is not null && CallSign.IsUsable(normalized))
        {
            found.Add(normalized);
        }
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var isPart = i < value.Length && (char.IsAsciiLetterOrDigit(value[i]) || value[i] == '/');
            if (isPart && start < 0)
            {
                start = i;
            }
            else if (!isPart && start >= 0)
            {
                yield return value[start..i];
                start = -1;
            }
        }
    }
}
