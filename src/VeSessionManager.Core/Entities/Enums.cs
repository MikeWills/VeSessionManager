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

public enum PaymentReason
{
    InitialExam,
    Retest
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
