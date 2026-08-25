using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// The real "could this rule have acted on this yet" floor for a trigger scanner — not just
/// <see cref="MessageRule.CreatedUtc"/> on its own (2026-08-25).
///
/// <para>Every scanner used to bound eligibility by <c>rule.CreatedUtc</c> alone. That's still right
/// for "a rule just created shouldn't fire for people already past the moment," but it missed two
/// other off-to-on transitions that deserve the same treatment: a rule switched back on after being
/// disabled, and a team's email configured for the first time. Mike, 2026-08-25, after a beta
/// candidate who'd registered before email was ever turned on got a confirmation the moment it was:
/// <i>"it's not supposed to send any backlog of email."</i> And, on a rule's own switch: <i>"if a
/// message is off then I turn on, it's not supposed to send backlog either."</i></para>
///
/// <para><b>Deliberately narrower than "no backlog on enable" everywhere.</b> Zoom, Discord and Square
/// still backfill the moment they're configured — a session ingested while Zoom was off still gets
/// its meeting created on the next poll, and that's correct: those create a resource that has to
/// exist regardless of when config caught up. Messaging is different because the thing being
/// protected is a person's inbox, not a resource — telling someone about something old the moment
/// notifications get switched on is the failure being avoided here, not there.</para>
/// </summary>
public static class MessageRuleEligibility
{
    /// <summary>
    /// The latest of: <see cref="MessageRule.CreatedUtc"/>, <see cref="MessageRule.EnabledSinceUtc"/>
    /// (if this rule was ever switched back on), and — for an email rule only —
    /// <see cref="Team.EmailConfiguredUtc"/> (if this team's email was ever configured after having
    /// been off). A Discord rule never reads the last one; SMTP being off has nothing to do with a
    /// channel post.
    /// </summary>
    public static DateTime FloorUtc(Team team, MessageRule rule)
    {
        var floor = rule.CreatedUtc;

        if (rule.EnabledSinceUtc is { } enabledSinceUtc && enabledSinceUtc > floor)
        {
            floor = enabledSinceUtc;
        }

        if (rule.Channel == MessageChannel.Email
            && team.EmailConfiguredUtc is { } emailConfiguredUtc
            && emailConfiguredUtc > floor)
        {
            floor = emailConfiguredUtc;
        }

        return floor;
    }
}
