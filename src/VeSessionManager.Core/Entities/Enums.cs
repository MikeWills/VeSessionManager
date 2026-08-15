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
