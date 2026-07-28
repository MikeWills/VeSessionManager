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
    /// Closed ("done") sessions for the team whose date falls within [startDateUtc, endDateUtc) —
    /// a completely separate feed from <see cref="GetTeamSessionsAsync"/>, which never surfaces
    /// closed sessions at all. See docs/examtools-api.md's "Closed sessions are a separate feed"
    /// section for how this was discovered.
    /// </summary>
    Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken);

    /// <summary>Registered applicants for one session, including PII — handle results per the PII logging rules.</summary>
    Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken);

    /// <summary>Full VE roster for one session (callsign + display name for every VE credited) — see ExamToolsFullExport.</summary>
    Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken);
}
