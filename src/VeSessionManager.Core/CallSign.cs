namespace VeSessionManager.Core;

/// <summary>
/// The one definition of "normalise a call sign", and — more importantly — of whether a string is a
/// call sign at all.
///
/// <para><b>Why the second question matters.</b> ExamTools' VE roster reports the literal string
/// <c>&lt;UNKNOWN&gt;</c> when it has no call sign for someone. Treated as an ordinary value it
/// behaves like a real call sign that many different people share, which is exactly what happened
/// live on 2026-08-07: the issue #142 merge fused HRCC's unidentified VE with MARC's into one
/// person, taking 88 sessions of history with them. Anything that matches people by call sign has to
/// ask <see cref="IsUsable"/> first.</para>
///
/// <para>The rule is structural rather than a list of known placeholders: an amateur call sign is
/// letters, digits and possibly a <c>/</c> prefix or suffix, so anything containing a character
/// outside that set is not a call sign, whatever it is. That catches
/// <c>&lt;UNKNOWN&gt;</c>, <c>N/A</c>-style markers with punctuation, and the next placeholder
/// ExamTools invents, without anyone having to predict it.</para>
///
/// <para>Also the helper the 2026-08-03 audit's T25 asked for — <c>Trim().ToUpperInvariant()</c> was
/// re-typed at six call sites, one of which forgot the Trim.</para>
/// </summary>
public static class CallSign
{
    /// <summary>Upper-invariant and trimmed, or null when the input is blank or is not call-sign-shaped.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToUpperInvariant();
        return IsUsable(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// Whether a value can be used to identify a person. False for placeholders and anything else
    /// that is not call-sign-shaped.
    /// <para>Deliberately permissive about the shape beyond the character set — this is not a
    /// validator for whether FCC would issue the call sign, only a guard against treating a
    /// non-call-sign as an identity.</para>
    /// </summary>
    public static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // Must contain at least one digit and one letter — every amateur call sign does, and it rules
        // out word-shaped placeholders like "UNKNOWN" or "NONE" that would otherwise pass the
        // character test below.
        var hasDigit = false;
        var hasLetter = false;

        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
            }
            else if (char.IsAsciiLetter(character))
            {
                hasLetter = true;
            }
            else if (character != '/')
            {
                return false;
            }
        }

        return hasDigit && hasLetter;
    }
}
