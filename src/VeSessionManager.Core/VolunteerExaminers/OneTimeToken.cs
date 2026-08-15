using System.Security.Cryptography;
using System.Text;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Minting and hashing the single-use tokens behind the VE self-service link and the VE email-change
/// confirmation (#309, DUP-12).
///
/// <para>Both services had their own copy, and the copies had already drifted cosmetically — one
/// used an imported <c>SHA256</c>, the other fully-qualified it — which is the warning sign the issue
/// filed them under. Cosmetic drift in two implementations of the same security primitive is how
/// substantive drift starts, because by then nobody reads them as the same code.</para>
///
/// <para><b>The properties here are the security of both features, so they are stated rather than
/// implied.</b> 32 bytes from a CSPRNG: guessing one is not a threat model anyone has to reason
/// about. Hex-encoded so it survives a URL and an email client untouched. Stored as SHA-256, never
/// in the clear — a database or backup leak must not hand over live credentials, and these tokens
/// authenticate: one reaches a VE's own contact details, the other confirms a change of the address
/// that owns the account.</para>
///
/// <para>No salt and no work factor, deliberately. Those defend low-entropy secrets people choose;
/// this is 256 bits of randomness with a short life, where a plain digest is the right tool and a
/// slow one would only cost the verifying request.</para>
/// </summary>
public static class OneTimeToken
{
    /// <summary>The raw token — sent, never stored. Store <see cref="Hash"/> of it.</summary>
    public static string Mint() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// What goes in the database, and what a redemption compares against. Lower-cased hex on both
    /// sides so a lookup is a plain string equality the database can index.
    /// </summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}
