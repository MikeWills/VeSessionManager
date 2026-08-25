using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// One subject a rule is due to fire for, with everything the dispatcher needs and nothing it has to
/// know the trigger to interpret.
/// </summary>
/// <param name="SubjectId">The <see cref="Candidate"/> or <see cref="Payment"/> id — see <paramref name="SubjectType"/>. Half of the idempotency key.</param>
/// <param name="CandidateEmail">
/// The candidate's own address, or null when they have none. Supplied even for a rule addressed to
/// the team's admin inbox, because whether the candidate is reachable is not what decides where an
/// internal notice goes.
/// </param>
/// <param name="Placeholders">Built by the scanner, which is the thing that loaded the graph. The dispatcher passes them straight to <c>EmailTemplateRenderer</c>.</param>
/// <param name="StampLegacySentUtc">
/// Sets whichever <c>Candidate.…SentUtc</c> column this trigger used to own, closing over the tracked
/// entity. Null for a trigger that never had one.
///
/// <para>Those columns are no longer authoritative — <see cref="MessageRuleRun"/> is — but the
/// candidate Email history screen still renders them, so they keep being written. Handing the
/// dispatcher a delegate rather than an enum keeps it from having to know which column belongs to
/// which trigger, which is exactly the knowledge that would rot when the next trigger is added.</para>
/// </param>
public sealed record MessageSubject(
    int SubjectId,
    MessageSubjectType SubjectType,
    string? CandidateEmail,
    IReadOnlyDictionary<string, string> Placeholders,
    Action<DateTime>? StampLegacySentUtc = null)
{
    /// <summary>
    /// How this subject appears in a <see cref="MessageFanOut.SingleDigest"/> post's
    /// <c>{{Subjects}}</c> list (#401 PR4).
    ///
    /// <para>Read off the placeholders rather than passed in by each scanner, so a digest names people
    /// exactly as every template already does and no scanner has to remember a second label. Falls
    /// back to the id — a digest line that says "#412" is poor, and a blank bullet is worse.</para>
    /// </summary>
    /// <summary>
    /// The call sign on this subject's session, for a rule whose Reply-To is
    /// <see cref="MessageReplyToSource.SessionLead"/> (#401 PR4). Null when the scanner did not load a
    /// session, or the session names no lead.
    ///
    /// <para>Carried here rather than looked up by the dispatcher because the scanner already has the
    /// session in hand — the alternative is a second query per subject for a field that was two joins
    /// away a moment ago.</para>
    /// </summary>
    public string? SessionLeadCallSign { get; init; }

    /// <summary>
    /// The session this subject belongs to, for <see cref="MessageFanOut.PerSession"/> grouping and
    /// the session tokens that come with it. Null when the scanner loaded no session — a
    /// payment-subject rule, say.
    ///
    /// <para>Carried by the scanner rather than looked up per subject by the dispatcher, for the same
    /// reason as <see cref="SessionLeadCallSign"/>: the scanner already has the session in hand.</para>
    /// </summary>
    public MessageSessionContext? Session { get; init; }

    public string DigestLabel =>
        Placeholders.TryGetValue("CandidateName", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"#{SubjectId}";
}

/// <summary>
/// What a <see cref="MessageFanOut.PerSession"/> message needs to know about the session it is about.
/// </summary>
/// <param name="SessionId">Groups the subjects. Also the only field required — the rest are for rendering.</param>
/// <param name="Title">The session's own name, as the app shows it.</param>
/// <param name="ScheduledStartUtc">Rendered through <c>SessionTimeFormatter</c>, so a channel post says Eastern like every screen does.</param>
/// <param name="RegisteredCandidateCount">
/// Candidates registered on the session — <b>not</b> the number of subjects this rule is firing for.
/// The two differ constantly: subjects are filtered by having an email, not being purged, and not
/// already having a terminal run for this rule. #116 asks for "x candidates registered to test", which
/// is this number and not that one.
/// </param>
public sealed record MessageSessionContext(
    int SessionId,
    string Title,
    DateTime ScheduledStartUtc,
    int RegisteredCandidateCount);

/// <summary>
/// Answers "which subjects is this rule due to fire for, right now" for one trigger point (#401).
///
/// <para><b>Every scanner owes three things</b>, and each of them was learned the hard way by the
/// hardcoded send it replaces:</para>
/// <list type="number">
/// <item>the guards its predecessor had — the recent-session bound, the payment eligibility window,
/// the PII-purge and cancelled-session exclusions. These belong in the trigger machinery <i>once</i>,
/// not in each rule a team writes;</item>
/// <item>excluding subjects that already have a <b>terminal</b>
/// <see cref="MessageRuleRun"/> for this rule (see <see cref="MessageRuleOutcome"/> — a failed
/// attempt is deliberately not terminal, so it is returned again);</item>
/// <item>bounding by <see cref="MessageRuleEligibility.FloorUtc"/> (not <see cref="MessageRule.CreatedUtc"/>
/// alone), so a rule never fires for a subject whose moment passed before the rule existed, was last
/// switched back on, or — for an email rule — before the team's email was configured.</item>
/// </list>
/// </summary>
public interface IMessageTriggerScanner
{
    MessageTrigger Trigger { get; }

    /// <param name="onlySessionId">Restrict to one session's subjects — the session-detail refresh button. Null scans the whole team.</param>
    Task<IReadOnlyList<MessageSubject>> ScanAsync(
        Team team,
        MessageRule rule,
        EmailSettings emailSettings,
        DateTime nowUtc,
        int? onlySessionId,
        CancellationToken cancellationToken);
}
