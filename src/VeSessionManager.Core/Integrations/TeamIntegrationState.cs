using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Integrations;

/// <summary>
/// "This team has Zoom switched off" — said once, not every poll (#64).
///
/// <para><b>The problem this exists for.</b> Every mute gate sits next to an existing
/// <c>IsConfigured</c> check whose established pattern is "skip quietly with one aggregate INFO line,
/// leave the tracking field null, retry next poll". That is right for an admin who has not finished
/// setup and wrong for a deliberate, indefinite switch: reusing it means a disabled integration
/// re-attempts and re-logs forever and never settles. A dev team would produce a log line every tick
/// about something that is off on purpose, which trains people to ignore the log.</para>
///
/// <para>So a suppressed action is logged when the state <i>changes</i> — including the first time
/// after a restart, which is what "once at startup" means for a process that holds this in memory —
/// and is silent after that. The Worker log still shows what would have happened, once.</para>
///
/// <para>Registered as a singleton, deliberately: the whole value is remembering across the scoped
/// lifetimes that background jobs create per tick. It holds no personal data — a team id, an enum,
/// and a bool.</para>
/// </summary>
public class TeamIntegrationState(ILogger<TeamIntegrationState> logger)
{
    private readonly ConcurrentDictionary<(int TeamId, TeamIntegration Integration), bool> lastLogged = new();

    /// <summary>
    /// Whether the call should go ahead. Returns false for a muted integration, logging the mute only
    /// when it is new information.
    ///
    /// <para>Call this <b>before</b> the <c>IsConfigured</c> check, not after: a team that has
    /// deliberately switched Zoom off should not also be told it has not finished configuring Zoom.
    /// The two answers are both true and only one of them is useful.</para>
    /// </summary>
    /// <param name="action">
    /// What is being suppressed, in the log line — "creating a Zoom meeting", not "Zoom". Present
    /// tense and specific, because the line's job is to say what would have happened.
    /// </param>
    public bool ShouldCall(Team team, TeamIntegration integration, string action)
    {
        var enabled = team.IsEnabled(integration);

        // Logged on transition only. The dictionary is keyed by (team, integration) rather than
        // holding a set of muted pairs, so re-enabling is also a transition and also says so — an
        // admin who flips a switch back on gets confirmation in the log that it took effect.
        //
        // `seen` is tracked separately from `previous` deliberately: TryGetValue's out parameter is
        // `false` both when the entry says false and when there is no entry at all, so testing
        // `previous` alone announced "switched back on" for an ordinary enabled integration nobody
        // had ever muted. Caught by AnEnabledIntegrationLogsNothing — the quiet case has to stay
        // quiet, which is the entire point of this class.
        var seen = lastLogged.TryGetValue((team.Id, integration), out var previous);
        if (seen && previous == enabled)
        {
            return enabled;
        }

        lastLogged[(team.Id, integration)] = enabled;

        if (!enabled)
        {
            logger.LogInformation(
                "{Integration} is switched off for team {TeamId} ({TeamName}) — suppressing {Action}, and not retrying. "
                + "Nothing is queued while it is off: re-enabling starts fresh from that moment.",
                integration, team.Id, team.Name, action);
        }
        else if (seen)
        {
            logger.LogInformation(
                "{Integration} is switched back on for team {TeamId} ({TeamName}).", integration, team.Id, team.Name);
        }

        return enabled;
    }
}
