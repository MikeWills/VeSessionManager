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

    public static bool IsTerminal(this CandidateApplicationStatus status) => TerminalStatuses.Contains(status);
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
    PaymentUnpaid = 3
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
    SessionLead = 2,
    DiscordChannel = 3
}

/// <summary>
/// Whether a rule produces one message per subject or one message covering them all.
///
/// <para>Named explicitly rather than derived from <see cref="MessageChannel"/>, because getting it
/// wrong posts to a Discord room forty times or sends one email addressed to nobody.</para>
/// </summary>
public enum MessageFanOut
{
    PerRecipient = 0,
    PerSubject = 1
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

/// <summary>What a <see cref="MessageRuleRun.SubjectId"/> points at. A trigger has exactly one subject type; see <see cref="Messaging.MessageTriggerDefinitions"/>.</summary>
public enum MessageSubjectType
{
    Candidate = 0,
    Payment = 1
}
