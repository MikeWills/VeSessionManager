using System.Linq;

namespace VeSessionManager.Core.Entities;

public enum SessionStatus
{
    Active,
    Cancelled
}

public enum VecSubmissionStatus
{
    NotSubmitted,
    Submitted
}

public enum CandidateApplicationStatus
{
    Unmatched,
    Received,
    Granted,
    Failed,
    NotTested
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
    None,
    Technician,
    General,
    Extra
}

public enum PaymentReason
{
    InitialExam,
    Retest
}

/// <summary>
/// Whether FCC is currently holding a candidate's application for one of its own review processes
/// — sourced from FCC ULS's HS.dat History record's Code field, not a guess: RDLOFF/RDLCOM
/// ("Offlined for Red Light"/"Redlight Review Completed") and BQOFF/BQCOM ("Offlined for Basic
/// Qualification Review"/"Basic Qualification Review Completed") are FCC's own documented codes
/// (see uls_code_definitions). None means neither hold is currently active — most applications sit
/// briefly in a Red Light hold while their $35 fee is unpaid, which is normal, not a signal of a
/// problem; this only reflects the hold's *current* state (the most recent OFF/COM pair per USI),
/// not history. See FccUlsRecordParser/FccUlsWatcherService and docs/fcc-uls-watcher.md.
/// </summary>
public enum FccApplicationHoldReason
{
    None,
    RedLight,
    BasicQualification,
    RedLightAndBasicQualification
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
    Unknown,
    PendingVerification,
    Paid
}

public enum PaymentStatus
{
    Unpaid,
    Paid,
    NotApplicable
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
    SystemAdmin,
    TeamAdmin,
    SessionManager,
    TeamLead
}
