using System.Linq;

namespace VeSessionManager.Core.Entities;

// ---------------------------------------------------------------------------------------------
// EVERY PERSISTED ENUM BELOW HAS PINNED VALUES, AND MUST KEEP THEM.
//
// They are stored as integers. A member inserted mid-list silently renumbers every existing row --
// a Granted candidate quietly becomes Failed -- with no compile error and nothing to notice at
// runtime, because the database has no idea the meaning moved.
//
// Pinning was a no-op when it was done (2026-08-11): the numbers are the ordinals the rows had
// already been written with. It exists so that adding a member can only ever be safe. Append with
// the next free number. Never renumber, never reuse, never reorder.
//
// The same applies to HistoricalImportStatus, which lives in HistoricalImportRequest.cs.
// ---------------------------------------------------------------------------------------------


public enum SessionStatus
{
    Active = 0,
    Cancelled = 1
}

public enum VecSubmissionStatus
{
    NotSubmitted = 0,
    Submitted = 1
}

public enum CandidateApplicationStatus
{
    Unmatched = 0,
    Received = 1,
    Granted = 2,
    Failed = 3,
    NotTested = 4
}

/// <summary>
/// Single definition of which CandidateApplicationStatus values are "terminal" (settled — no
/// further FCC/session processing expected). Previously reimplemented independently in
/// SessionIngestionService/VecSubmissionReportService/PaymentReminderService/SessionActionService/
/// CandidateActionService with no shared source of truth. TerminalStatuses is a static array so a
/// LINQ `.Contains(...)` against it translates to SQL IN in EF Core queries; IsTerminal is for
/// in-memory checks on an already-materialized Candidate.
/// </summary>
public static class CandidateApplicationStatusExtensions
{
    public static readonly CandidateApplicationStatus[] TerminalStatuses =
    [
        CandidateApplicationStatus.Granted,
        CandidateApplicationStatus.Failed,
        CandidateApplicationStatus.NotTested
    ];

    /// <summary>
    /// Statuses that represent <b>a result worth sending to the VEC</b> — deliberately not the same
    /// set as <see cref="TerminalStatuses"/> (#423).
    ///
    /// <para>"Settled, stop chasing" and "there is paperwork to file" are different questions, and
    /// <c>NotTested</c> answers them differently: a no-show is settled, and produces nothing to
    /// submit. Counting it as submittable flagged sessions whose only candidate had withdrawn as
    /// pending submission — permanently, since the only way to clear the flag is to record a
    /// submission that never happened. Two upcoming sessions showed exactly that on beta, each
    /// reading "0 candidates" because the roster count excludes the withdrawn row that flagged it.</para>
    ///
    /// <para><b>Do not merge this back into <see cref="TerminalStatuses"/>.</b> That set is right for
    /// its own callers — the withdrawal guard and the scan filters, where a no-show genuinely is
    /// settled. Two questions, two lists.</para>
    /// </summary>
    public static readonly CandidateApplicationStatus[] SubmittableStatuses =
    [
        CandidateApplicationStatus.Granted,
        CandidateApplicationStatus.Failed
    ];

    public static bool IsTerminal(this CandidateApplicationStatus status) => TerminalStatuses.Contains(status);

    /// <summary>
    /// "Still waiting on an FCC grant" — passed the exam, not yet Granted/Failed/NotTested, still
    /// Unmatched or Received. The one definition of Applicant Status's "Pending" worklist (2026-08-26),
    /// shared with the bulk-email screen reached from it so the two can never quietly drift apart on
    /// who counts as still waiting.
    ///
    /// <para>Excludes a historically-imported session (#88) — in practice this rarely matters, since
    /// <c>MarkHistoricalCandidatesGranted</c> already auto-grants (terminal) every historical
    /// candidate at import time, but the exclusion is the structural backstop for whatever gap leaves
    /// one non-terminal anyway, rather than relying only on that other service's behavior.</para>
    /// </summary>
    public static IQueryable<Candidate> AwaitingFccGrant(this IQueryable<Candidate> candidates) =>
        candidates.Where(c => c.Tested
            && (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received)
            && c.Session.ImportedHistoricallyUtc == null);
}

/// <summary>
/// Written license class as of the FCC's element structure (Element 1/Morse code retired 2007, so
/// there's no class below Technician). Derived purely from which exam elements a candidate passed
/// this sitting (see ExamResultSyncService) — VE sessions never re-administer an element a candidate
/// already holds credit for, so the elements graded this sitting alone are enough to infer both the
/// class held walking in (Initial) and the class earned walking out (New), with no FCC ULS lookup
/// needed. None means "no prior amateur license."
/// </summary>
public enum LicenseClass
{
    None = 0,
    Technician = 1,
    General = 2,
    Extra = 3
}

public enum PaymentReason
{
    InitialExam = 0,
    Retest = 1
}

/// <summary>
/// Whether FCC is currently holding a candidate's application for one of its own review processes
/// — sourced from FCC ULS's HS.dat History record's Code field, not a guess: RDLOFF/RDLCOM
/// ("Offlined for Red Light"/"Redlight Review Completed") and BQOFF/BQCOM ("Offlined for Basic
/// Qualification Review"/"Basic Qualification Review Completed") are FCC's own documented codes
/// (see uls_code_definitions). None means neither hold is currently active — most applications sit
/// briefly in a Red Light hold while their $35 fee is unpaid, which is normal, not a signal of a
/// problem; this only reflects the hold's *current* state (the most recent OFF/COM pair per USI),
/// not history. See UlsWatcherService and docs/uls-watcher.md.
/// </summary>
public enum FccApplicationHoldReason
{
    None = 0,
    RedLight = 1,
    BasicQualification = 2,
    RedLightAndBasicQualification = 3
}

/// <summary>
/// Whether FCC's own fee-payment verification step (separate from the Red Light Rule debt system —
/// this is ULS's internal fee-validation workflow) has confirmed the candidate's $35 application fee.
/// Sourced from HS.dat's Code field, same as FccApplicationHoldReason: FVPOFF ("Offlined for Payment
/// Verification") vs FVPCNF ("Payment Confirmed") / FVPCOM ("Payment Verification Completed") — see
/// uls_code_definitions. Unknown means this application's history has no fee-verification event at
/// all yet (the common case for a very recently filed application — not itself a problem).
/// </summary>
public enum FccApplicationPaymentStatus
{
    Unknown = 0,
    PendingVerification = 1,
    Paid = 2
}

public enum PaymentStatus
{
    Unpaid = 0,
    Paid = 1,
    NotApplicable = 2
}

/// <summary>
/// Where a <see cref="Refund"/> has got to. Mirrors Square's own refund states, with one addition
/// this app needs and Square has no word for.
///
/// <para><b>A refund is not over when the API call returns.</b> Square answers immediately and then
/// processes: a card or bank-transfer refund can sit <c>PENDING</c> for up to 14 days before it
/// reaches <c>COMPLETED</c>, and it can still end at <c>REJECTED</c> or <c>FAILED</c>. Treating a
/// successful call as a finished refund is the single easiest mistake to make here, and it is
/// invisible — the screen says refunded and the buyer's card never sees it. Hence
/// <see cref="RefundStatusService"/>, which polls until a terminal state is observed.</para>
/// </summary>
public enum RefundStatus
{
    /// <summary>
    /// Recorded here but not yet accepted by Square — the row was written *before* the call, so this
    /// is also what a crash mid-call leaves behind. Distinguished from Square's own PENDING by
    /// <see cref="Refund.SquareRefundId"/> being null. Retried with the same idempotency key.
    /// </summary>
    Submitting = 0,

    /// <summary>Square accepted it and is processing. Not money returned yet.</summary>
    Pending = 1,

    /// <summary>Terminal, and the only one that means the buyer got their money.</summary>
    Completed = 2,

    /// <summary>Terminal — Square declined it.</summary>
    Rejected = 3,

    /// <summary>Terminal — Square errored (e.g. insufficient balance in the merchant account).</summary>
    Failed = 4
}

/// <summary>
/// Which colour scheme a <see cref="User"/> sees, stored on the account so it follows them to every
/// browser they sign in on rather than living only in that browser's localStorage.
///
/// <para><see cref="System"/> is the default and the only value nothing has to write: it means "no
/// explicit choice yet", and the browser's own <c>prefers-color-scheme</c> answers instead. Every
/// account created before this field existed lands here, which is the intended behavior rather than
/// a migration gap.</para>
///
/// <para>The toggle in the chassis only ever writes <see cref="Light"/> or <see cref="Dark"/> — once
/// someone has clicked it they have made a choice, and silently reverting to the OS on their next OS
/// theme change is the behavior the choice was expressing a preference against. There is
/// deliberately no way back to <see cref="System"/> from the toggle; a three-state control whose
/// current state is invisible ("is this dark because I picked dark, or because it is 9pm?") costs
/// more than it buys here.</para>
/// </summary>
public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Renamed/expanded for Phase 9a (2026-07-21) from the spec's original 3-role model
/// (Admin/SessionManager/TeamLead) — "Admin" didn't fit well once the multi-team foundation
/// (Phase 6.5) gave each Team its own credentials/settings. See docs/admin-auth.md.
///
/// Hierarchy, each a superset of the next for session-level access (see SessionAccessScope):
/// SystemAdmin (renamed from "Admin"; deployment-wide, creates Teams, grants TeamAdmin)
///   ⊇ TeamAdmin (new; own team's settings + everything SessionManager can do; grants
///     SessionManager/TeamLead within their team)
///     ⊇ SessionManager (unchanged; own team's sessions).
/// TeamLead (unchanged) is a separate, narrower, read-only branch — scoped via
/// User.ManagedByUserId to whichever manager (SessionManager or TeamAdmin) they're assigned to.
/// </summary>
public enum UserRole
{
    SystemAdmin = 0,
    TeamAdmin = 1,
    SessionManager = 2,
    TeamLead = 3
}

/// <summary>
/// A moment when a condition first becomes true for a subject, and the thing a team hangs its own
/// <see cref="MessageRule"/>s off (#401). See docs/trigger-points.md.
///
/// <para>Two mechanisms, and the difference decides how a scanner is written rather than being a
/// label: a <b>state</b> trigger fires when a stored value changes (the moment is a stored
/// timestamp), and a <b>time-relative</b> trigger fires a configurable number of hours either side of
/// an anchor instant. <see cref="Messaging.MessageTriggerDefinitions"/> is where that is recorded.</para>
/// </summary>
public enum MessageTrigger
{
    /// <summary>A candidate appeared in ExamTools' feed. Replaces the hardcoded RegistrationConfirmation send.</summary>
    CandidateRegistered = 0,

    /// <summary>Time-relative, before <c>Session.ScheduledStartUtc</c>. Replaces the 24-hour DayBeforeReminder.</summary>
    BeforeSessionStart = 1,

    /// <summary>Time-relative, after <c>Candidate.ApplicationDateEnteredUtc</c>, while FCC still wants its own fee. Replaces the 5-day FccFeeReminder5Day.</summary>
    FccFeeOutstanding = 2,

    /// <summary>Time-relative, after the application/result anchor, while a Square payment is still unpaid. Replaces the 10-day PaymentExpirationNotice.</summary>
    PaymentUnpaid = 3,

    /// <summary>
    /// A candidate has sat their exam — <c>Candidate.Tested</c>, from either the Session Manager
    /// marking a session completed or the automatic exam-result sync (#401 PR3). New: nothing sent
    /// here before, and no rule is seeded for it.
    /// </summary>
    CandidateTested = 4,

    /// <summary>
    /// The FCC has granted a license from this session — the natural home for a welcome email, and
    /// the first trigger where <c>{{CallSign}}</c> resolves to anything (#401 PR3).
    /// </summary>
    LicenseGranted = 5,

    /// <summary>
    /// A candidate declared a felony disclosure on their application (#401 PR3).
    ///
    /// <para><b>Declaration, not completion, and no rule is seeded.</b> #221 deliberately took this
    /// email <i>off</i> an automatic path because riding along with "mark session completed" meant it
    /// could only ever arrive after the exam — the point at which the candidate can no longer easily
    /// ask anyone about it. Offering it here as an opt-in fixes the timing rather than reinstating
    /// the mistake; a team that wants it automatic gets it before the session, not after.</para>
    /// </summary>
    FelonyDisclosureDeclared = 6,

    /// <summary>
    /// A person composing an email to candidates on Session Detail (#144).
    ///
    /// <para><b>Manual sends are trigger points too</b> (Mike, 2026-08-21). Before this they were
    /// "templates", authored with no trigger — which is exactly why the editor could not say which
    /// tags were available: the answer depends on what sends it, and nothing knew yet. Once a manual
    /// send is a trigger like any other, the tag list is answerable everywhere.</para>
    /// </summary>
    ManualToCandidate = 7,

    /// <summary>A person composing an email to VEs from the VE Directory (#191). See <see cref="ManualToCandidate"/>.</summary>
    ManualToVe = 8,

    /// <summary>
    /// The "send felony disclosure instructions" button on a candidate (#221).
    ///
    /// <para>Its own trigger point rather than a shared manual one, because a button that sends one
    /// particular message <i>is</i> a moment — and giving it its own trigger is what lets the editor
    /// show the tags that apply to it. Repeatable by design: the timestamp on the candidate is
    /// display-only, not a guard.</para>
    /// </summary>
    ManualFelonyDisclosureInstructions = 9,

    /// <summary>The "send youth program instructions" button on a candidate. Same shape as <see cref="ManualFelonyDisclosureInstructions"/>, and repeatable for the same reason.</summary>
    ManualYouthProgramInstructions = 10,

    /// <summary>
    /// The exam fee is still unpaid and the session is coming up (Mike, 2026-08-25) — a candidate who
    /// has not paid cannot test, so a team may want to chase this before the session rather than
    /// after.
    ///
    /// <para><b>Not <see cref="PaymentUnpaid"/> repurposed</b> (that trigger, and the
    /// <c>Payment.ExpiredUnpaid</c> bookkeeping write its hours also drove, are gone entirely as of
    /// 2026-08-25 — see CLAUDE.md's "No fee, no test" Known Constraint). Its old clock started from
    /// the FCC application date, which for most candidates does not exist yet before their session —
    /// there would be nothing to anchor on. This is a new trigger with its own clock: the session's
    /// own start time, counting backward.</para>
    /// </summary>
    PaymentUnpaidBeforeSession = 11,

    /// <summary>
    /// Not a trigger point — a note on a run saying a person pressed a button (#417).
    ///
    /// <para>A hand-send has no moment to scan for, but <c>MessageRuleRun.Trigger</c> is not nullable
    /// and the run still has to exist, or the send is invisible to the candidate's email history.
    /// Numbered clear of the scan triggers and deliberately <b>absent from
    /// <c>MessageTriggerDefinitions.All</c></b>: the admin screens iterate that list, so this can never
    /// appear as something configurable, and <c>MessageTriggerDefinitions.For</c> throws for it because
    /// nothing should ask.</para>
    ///
    /// <para>Used only where the hand-send mirrors no real trigger. A resent confirmation records
    /// <see cref="CandidateRegistered"/>, because that is what the message <i>is</i>, however it was
    /// set off.</para>
    /// </summary>
    SentByHand = 100
}

/// <summary>How a <see cref="MessageRule"/> delivers. Discord is declared but not yet dispatchable — see <see cref="MessageRecipient"/>.</summary>
public enum MessageChannel
{
    Email = 0,
    Discord = 1
}

/// <summary>
/// Who a <see cref="MessageRule"/> addresses. <see cref="TeamAdminAddress"/> replaces the
/// PaymentExpirationNotice special case, which was the one hardcoded send that never went to a
/// candidate.
///
/// <para><b>Two of these cannot yet be dispatched.</b> <see cref="SessionLead"/> and
/// <see cref="DiscordChannel"/> are part of the agreed model and are declared here so the column
/// never has to be widened, but <c>MessageDispatchService</c> refuses them outright rather than
/// guessing. Nothing can create such a rule today — the seeder is the only writer.</para>
/// </summary>
public enum MessageRecipient
{
    Candidate = 0,
    TeamAdminAddress = 1,

    /// <summary>
    /// The VE running the session, from ExamTools' Team Lead field via <c>Session.TeamLeadCallSign</c>.
    /// Dispatchable since the trigger × recipient work — see <c>MessageRecipientResolver</c>.
    ///
    /// <para>⚠️ Not the same population as <see cref="SessionManagers"/>, despite "Team Lead = SM":
    /// this resolves through a <b>VE record</b> and the person may have no app account at all. That
    /// equivalence is true of the people and false of the plumbing.</para>
    /// </summary>
    SessionLead = 2,

    DiscordChannel = 3,

    /// <summary>Every app user with <c>TeamAdmin</c> on the rule's team.</summary>
    TeamAdmins = 4,

    /// <summary>
    /// Every app user with <c>SystemAdmin</c>. <b>Not team-scoped</b> — a SystemAdmin spans every team
    /// by definition, and requiring a <c>UserTeam</c> row would resolve to nobody for the one role
    /// always entitled to know.
    /// </summary>
    SystemAdmins = 5,

    /// <summary>Every app user with <c>SessionManager</c> on the rule's team. Mike, 2026-08-20: "All SMs is a third role option."</summary>
    SessionManagers = 6
}

/// <summary>
/// Whether a rule produces one message per subject or one message covering them all.
///
/// <para>Named explicitly rather than derived from <see cref="MessageChannel"/>, because getting it
/// wrong posts to a Discord room forty times or sends one email addressed to nobody.</para>
/// </summary>
public enum MessageFanOut
{
    /// <summary>One message per subject, addressed to that subject's recipient. The default for every rule, email or Discord.</summary>
    PerRecipient = 0,

    /// <summary>
    /// One message covering every subject in the batch across every session — a true digest. Only
    /// meaningful for a channel nobody is individually addressed on, which today means Discord: on
    /// email it would be one message to one address listing every other candidate, a disclosure
    /// rather than a feature. <see cref="PerSession"/> is the version usable on email (#491), because
    /// grouping by session is what gives it a single recipient to be about.
    ///
    /// <para><b>Renamed from <c>PerSubject</c> in PR4</b>, value unchanged so nothing stored moves.
    /// The old name was ambiguous in the one place ambiguity is expensive: "per subject" reads as
    /// "one per candidate", which is the opposite of what it selects and is exactly the forty-posts
    /// mistake the <see cref="MessageFanOut"/> field exists to prevent.</para>
    /// </summary>
    SingleDigest = 1,

    /// <summary>
    /// One message per <b>session</b>, covering that session's subjects.
    ///
    /// <para>The middle ground <see cref="SingleDigest"/> could not express. A digest batches
    /// everything one scan returned across <i>all</i> of a team's sessions, which is why a post could
    /// never say "x candidates registered to test at xx:xx" — there was no single session for the
    /// sentence to be about. Grouping restores that, and with it the session's own tokens.</para>
    ///
    /// <para>Subjects with no session are grouped together and rendered without the session tokens,
    /// rather than dropped: a payment-subject rule set to PerSession should still send something.</para>
    ///
    /// <para><b>Usable on email too, unlike <see cref="SingleDigest"/> (#491).</b> Grouping by session
    /// is what a batched message needs to have a single, sensible To — the VE running that session, a
    /// team-admin role, and so on — which is exactly what a whole-team digest can't offer. Refused only
    /// when addressed to <see cref="MessageRecipient.Candidate"/>, the one recipient a per-session
    /// summary about several candidates has no single address for.</para>
    /// </summary>
    PerSession = 2
}

/// <summary>
/// What happened when a rule fired for one subject — recorded on <see cref="MessageRuleRun"/>.
///
/// <para><b>Only <see cref="Sent"/> and <see cref="Suppressed"/> are terminal</b>, and that
/// distinction is the whole idempotency model: a scanner excludes subjects that already have a
/// terminal run, and returns the others again. See <see cref="MessageRuleRun"/>.</para>
/// </summary>
public enum MessageRuleOutcome
{
    /// <summary>Handed to SMTP without error. Terminal.</summary>
    Sent = 0,

    /// <summary>The team has email switched off. Terminal — the settle-without-doing rule: nothing is queued while it is off, so re-enabling starts fresh from that moment rather than flushing a backlog.</summary>
    Suppressed = 1,

    /// <summary>There was no address to send to. <b>Not</b> terminal: an address added later should still get the message.</summary>
    NoRecipient = 2,

    /// <summary>The render or the send failed. <b>Not</b> terminal — retried on the next scan, exactly as a failed send has always been.</summary>
    Failed = 3
}

/// <summary>
/// Where a rule's Reply-To comes from (#401 PR4).
///
/// <para>Note what is absent: a From source. Changing the From address means SPF/DKIM/DMARC on a
/// domain this app does not control, and getting it wrong sends the mail to spam silently. Reply-To
/// carries no such risk and is what "can it come from the session lead" actually means.</para>
/// </summary>
public enum MessageReplyToSource
{
    /// <summary>The team's configured Reply-To. What every message did before this field existed.</summary>
    EmailSettings = 0,

    /// <summary>
    /// The session's lead VE, resolved from <c>Session.TeamLeadCallSign</c>.
    ///
    /// <para>Falls back to the team's own address when the lead cannot be resolved — no call sign on
    /// the session, a placeholder like ExamTools' literal <c>&lt;UNKNOWN&gt;</c>, no matching VE, or a
    /// VE with no email. A reply that reaches the team is worse than one that reaches the lead;
    /// a reply that reaches nobody is worse than both.</para>
    /// </summary>
    SessionLead = 1,

    /// <summary>A fixed address typed on the rule.</summary>
    Custom = 2
}

/// <summary>What a <see cref="MessageRuleRun.SubjectId"/> points at. A trigger has exactly one subject type; see <see cref="Messaging.MessageTriggerDefinitions"/>.</summary>
public enum MessageSubjectType
{
    Candidate = 0,
    Payment = 1
}
