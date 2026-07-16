namespace VeSessionManager.Core.Entities;

public enum SessionStatus
{
    Active,
    Cancelled
}

public enum ArrlSubmissionStatus
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

public enum UserRole
{
    Admin,
    SessionManager,
    TeamLead
}
