namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// Read-only client for the ExamTools/HamStudy VE API. Wrapped in an interface so ingestion
/// logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface IExamToolsClient
{
    /// <summary>
    /// Every "pend" (not-yet-closed) session for the team — despite the name, this endpoint never
    /// returns a closed ("done") session, no matter how far in the past; see
    /// <see cref="GetTeamClosedSessionsAsync"/> for those. Confirmed live 2026-07-28 against real
    /// HRCC data — see docs/examtools-api.md.
    /// </summary>
    Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Closed ("done") sessions for the team on or between the two dates — <b>both ends
    /// inclusive</b>. A completely separate feed from <see cref="GetTeamSessionsAsync"/>, which
    /// never surfaces closed sessions at all. See docs/examtools-api.md's "Closed sessions are a
    /// separate feed" section for how this was discovered.
    ///
    /// <para><b>ExamTools' own bound is exclusive</b> and the implementation compensates by asking
    /// for one day more. This used to be documented as half-open and left to the caller, which cost
    /// months of silent data loss: the historical import chunks by calendar month and passed an
    /// inclusive month-end, so <i>every chunk dropped its final day</i> — found 2026-08-10 when a VE
    /// who had worked on 31 May appeared to have been inactive since the previous August. An API
    /// whose contract is the opposite of what every caller means is the wrong place to be clever, so
    /// the adjustment lives here, once, rather than at each call site.</para>
    /// </summary>
    Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateInclusiveUtc, CancellationToken cancellationToken);

    /// <summary>Registered applicants for one session, including PII — handle results per the PII logging rules.</summary>
    Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken);

    /// <summary>Full VE roster for one session (callsign + display name for every VE credited) — see ExamToolsFullExport.</summary>
    Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken);

    /// <summary>One applicant's full detail, including graded exam results — see ExamToolsApplicantDetail. Handle results per the PII logging rules, same as GetSessionApplicantsAsync.</summary>
    Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken);
}
