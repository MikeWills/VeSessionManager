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
///
/// <para><b>Persisted as an integer, so these values are pinned and must keep their numbers</b> —
/// the rule stated in <c>Enums.cs</c>, which this enum was missed by because it lives in its own
/// file (issue #285, pinned 2026-08-11). The consequence here is the sharpest in the codebase:
/// renumbering these two silently flips every stored team between Sandbox and Production. A team
/// that was taking test payments would start taking real ones, or stop taking real ones, with no
/// migration and nothing in the audit log. Append new members, never insert.</para>
/// </summary>
public enum SquareApiEnvironment
{
    Sandbox = 0,
    Production = 1
}
