namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// Read-only client for the ExamTools/HamStudy VE API. Wrapped in an interface so ingestion
/// logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface IExamToolsClient
{
    /// <summary>All sessions visible to the given team, upcoming and past.</summary>
    Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken);

    /// <summary>Registered applicants for one session, including PII — handle results per the PII logging rules.</summary>
    Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken);
}
