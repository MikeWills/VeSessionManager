using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The per-team Square SDK client is rebuilt when the credentials it was built from change (#252).
///
/// <para><b>The bug this replaces was silent and operational.</b> The cache was keyed by TeamId
/// alone, so in the long-lived Worker process a credential edit in Team Settings had no effect until
/// a restart. CLAUDE.md's own post-deploy step is "set live teams back to Production in Team
/// Settings" — that did nothing. Rotating an access token was worse: every payment link kept failing
/// against the revoked token with nothing to indicate why.</para>
///
/// <para>Asserting on <b>reference identity</b> because that is the whole behavior: the same client
/// reused, or a new one built. Anything further would need a live Square account.</para>
/// </summary>
public class SquareClientCacheTests
{
    private static SquareClient Create() => new(NullLogger<SquareClient>.Instance);

    private static SquareCredentials Credentials(
        string accessToken = "token-1",
        SquareApiEnvironment environment = SquareApiEnvironment.Sandbox,
        int teamId = 1) =>
        new(teamId, accessToken, "location-1", environment);

    [Fact]
    public void UnchangedCredentialsReuseTheSameClient()
    {
        var client = Create();

        var first = client.GetOrCreateClient(Credentials());
        var second = client.GetOrCreateClient(Credentials());

        Assert.Same(first, second);
    }

    /// <summary>Rotating a token: the old client holds a revoked secret and must not survive.</summary>
    [Fact]
    public void ARotatedAccessTokenRebuildsTheClient()
    {
        var client = Create();

        var before = client.GetOrCreateClient(Credentials(accessToken: "old-token"));
        var after = client.GetOrCreateClient(Credentials(accessToken: "new-token"));

        Assert.NotSame(before, after);
    }

    /// <summary>
    /// The documented post-deploy step — switching a team from Sandbox to Production — which
    /// previously kept talking to Sandbox until the Worker restarted.
    /// </summary>
    [Fact]
    public void SwitchingEnvironmentRebuildsTheClient()
    {
        var client = Create();

        var sandbox = client.GetOrCreateClient(Credentials(environment: SquareApiEnvironment.Sandbox));
        var production = client.GetOrCreateClient(Credentials(environment: SquareApiEnvironment.Production));

        Assert.NotSame(sandbox, production);
    }

    /// <summary>
    /// Rebuilding one team's client must not disturb another's. The cache is per team because each
    /// team is a separate Square merchant account.
    /// </summary>
    [Fact]
    public void RebuildingOneTeamLeavesOtherTeamsAlone()
    {
        var client = Create();

        var teamOne = client.GetOrCreateClient(Credentials(teamId: 1));
        var teamTwo = client.GetOrCreateClient(Credentials(teamId: 2));

        client.GetOrCreateClient(Credentials(teamId: 1, accessToken: "rotated"));

        Assert.Same(teamTwo, client.GetOrCreateClient(Credentials(teamId: 2)));
        Assert.NotSame(teamOne, client.GetOrCreateClient(Credentials(teamId: 1, accessToken: "rotated")));
    }
}
