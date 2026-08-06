namespace VeSessionManager.Core.Entities;

/// <summary>
/// Which Square API a team's credentials belong to.
///
/// <para><b>Per team, not per deployment.</b> This was originally a global
/// <c>SquareOptions.Environment</c> setting, on the reasoning that Sandbox-vs-Production is a
/// whole-deployment choice. It isn't: a Square access token is <i>issued for</i> an environment and
/// only authenticates against that host, so the environment is a property of the credential set —
/// and credentials are per team. With one global switch, a deployment running a real team on
/// Production could not also keep a test team on Sandbox; the test team's token simply failed.</para>
///
/// <para>Defaults to <see cref="Sandbox"/> so a newly created team can never take real money before
/// someone has deliberately said so.</para>
/// </summary>
public enum SquareApiEnvironment
{
    Sandbox,
    Production
}
