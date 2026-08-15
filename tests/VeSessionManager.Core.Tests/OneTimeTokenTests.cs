using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The properties both token flows rest on (#309, DUP-12) — the VE self-service link and the VE
/// email-change confirmation. Previously each service had its own copy of this, and the copies had
/// already drifted cosmetically.
/// </summary>
public class OneTimeTokenTests
{
    /// <summary>256 bits, hex-encoded. Guessing one should not be a threat model anyone reasons about.</summary>
    [Fact]
    public void AMintedTokenIs256BitsOfHex()
    {
        var token = OneTimeToken.Mint();

        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public void EveryMintedTokenIsDifferent()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => OneTimeToken.Mint()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    /// <summary>
    /// Redemption looks the token up by hash, so the same input must always produce the same output —
    /// otherwise a freshly issued link would never match its own stored row.
    /// </summary>
    [Fact]
    public void HashingIsDeterministic()
    {
        var token = OneTimeToken.Mint();

        Assert.Equal(OneTimeToken.Hash(token), OneTimeToken.Hash(token));
    }

    /// <summary>
    /// The stored value must not be the credential. A database or backup leak has to hand over
    /// something inert, not a live sign-in link.
    /// </summary>
    [Fact]
    public void TheHashIsNotTheToken()
    {
        var token = OneTimeToken.Mint();
        var hash = OneTimeToken.Hash(token);

        Assert.NotEqual(token, hash);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void DifferentTokensHashDifferently()
    {
        Assert.NotEqual(OneTimeToken.Hash(OneTimeToken.Mint()), OneTimeToken.Hash(OneTimeToken.Mint()));
    }

    /// <summary>
    /// Lower-cased hex on both sides, so a lookup stays a plain string equality the database can
    /// index — SQLite's `=` on TEXT is case-sensitive, so a mixed-case digest would simply never match.
    /// </summary>
    [Fact]
    public void HashesAreLowerCaseHex()
    {
        var hash = OneTimeToken.Hash("ABCdef123");

        Assert.Equal(hash.ToLowerInvariant(), hash);
    }
}
