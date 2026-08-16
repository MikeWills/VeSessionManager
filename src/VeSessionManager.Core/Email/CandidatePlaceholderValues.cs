using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Core.Email;

/// <summary>
/// The <c>{{Token}}</c> values available to a <b>hand-composed</b> candidate email (#144) — the draft
/// someone writes on a session's Email candidates screen, starting from a template.
///
/// <para><b>Why this exists separately from the dictionaries inside
/// <see cref="CandidateNotificationService"/>.</b> Each of those is built for one template key and
/// knows only that template's tokens, which works because the code decides what it is sending. Here
/// it does not: the body is whatever the sender typed, possibly from a template a team invented this
/// morning, so the token set has to be a property of "a candidate" rather than of a particular
/// email.</para>
///
/// <para><b>Deliberately no payment links.</b> The automated emails carry
/// <c>{{PaymentLinkUrl}}</c>/<c>{{OutstandingPaymentLinkUrl}}</c> because they are sent at the moment
/// those links are live and relevant. A hand-composed message goes out whenever someone decides to
/// send it — typically after the session — and a checkout link that is expired, already paid, or
/// simply blank is worse than no link at all. Anything needing one has an automated template that
/// already sends it.</para>
///
/// <para>The date goes through <see cref="SessionTimeFormatter.ForCandidate"/>, which is the one
/// thing here that has actually drifted before: candidate email rendered UTC for months while every
/// screen rendered Eastern (#205), because the formatter lived somewhere Core could not reach.</para>
/// </summary>
public static class CandidatePlaceholderValues
{
    /// <summary>What the compose screen offers as insertable chips, and the only tokens substituted below. Must match <see cref="EmailTemplatePlaceholders"/>'s entry for the getting-started key.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["CandidateName", "CandidateFirstName", "CallSign", "SessionDate", "TeamName"];

    /// <param name="candidate">Requires <c>Session</c> loaded.</param>
    /// <param name="teamName">Passed in rather than read off <c>candidate.Session.Team</c>: the caller has the team already, and one send resolves this for every recipient.</param>
    public static Dictionary<string, string> For(Candidate candidate, string teamName) => new()
    {
        ["CandidateName"] = candidate.Name ?? "",
        ["CandidateFirstName"] = candidate.FirstName ?? "",
        // Empty for most of a session's candidates most of the time — a new licensee's call sign
        // arrives from the FCC days afterwards. The compose screen warns rather than letting someone
        // discover the gap in a sent email.
        ["CallSign"] = candidate.CallSign ?? "",
        ["SessionDate"] = SessionTimeFormatter.ForCandidate(candidate.Session.ScheduledStartUtc),
        ["TeamName"] = teamName
    };
}
