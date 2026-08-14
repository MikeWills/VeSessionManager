using System.Security.Cryptography;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// An RFC 6238 TOTP generator, standing in for the phone.
///
/// <para><b>Why this exists rather than asking Identity.</b> The obvious move is
/// <c>UserManager.GenerateTwoFactorTokenAsync(user, "Authenticator")</c> — and it returns an
/// <b>empty string</b>. Identity's <c>AuthenticatorTokenProvider</c> deliberately cannot generate:
/// the whole point of the scheme is that only the authenticator app holds the ability to produce a
/// code, and the server only ever validates. Using it produced a test that failed against completely
/// correct code, which is worth recording because the method name reads exactly like it would
/// work.</para>
///
/// <para>So the test computes the code the way a phone would, from the same base32 secret Identity
/// stores: HMAC-SHA1 over a 30-second counter, dynamically truncated to six digits. That means these
/// tests exercise the real validation path rather than a stub — if the app's TOTP handling breaks,
/// they fail.</para>
/// </summary>
internal static class Totp
{
    private const int Digits = 6;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>The code an authenticator app would be showing right now for this key.</summary>
    internal static string Generate(string base32Key, DateTimeOffset? at = null)
    {
        var counter = (long)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / Step.TotalSeconds);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(DecodeBase32(base32Key));
        var hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation, RFC 4226 §5.4: the low nibble of the last byte picks the offset.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString().PadLeft(Digits, '0');
    }

    /// <summary>
    /// RFC 4648 base32, which is the alphabet Identity's authenticator key uses. Written out rather
    /// than pulled in, because a dependency for thirty lines of bit-shuffling in a test project is a
    /// poor trade.
    /// </summary>
    private static byte[] DecodeBase32(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var cleaned = input.TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
        var bits = 0;
        var value = 0;
        var output = new List<byte>(cleaned.Length * 5 / 8);

        foreach (var c in cleaned)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                throw new FormatException($"'{c}' is not a base32 character — the authenticator key is malformed.");
            }

            value = (value << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
